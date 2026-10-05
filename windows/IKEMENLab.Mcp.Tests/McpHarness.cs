using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;
using IKEMENLab.Tests;
using Xunit;

namespace IKEMENLab.Mcp.Tests;

/// <summary>
/// An isolated MCP server over the ComboGuy fixture character: its own IKEMEN root, its own data folder (names, sequences, experiments, MCP runs, settings),
/// its own sandbox folder and — when asked — its own runtime lock. The engine is <see cref="TestEngine"/>, which answers with the Core tests' mock-engine
/// traces re-identified to the plan the sandbox really received (the real driver and probe ran those plans).
/// </summary>
internal sealed class McpHarness : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly List<string> _cleanup = [];

    public McpHarness(bool withBroker = false, TimeSpan? queueTimeout = null)
    {
        Root = _fx.Root;
        void W(string rel, string text)
        {
            var p = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("Ikemen_GO.exe", "exe");
        W("data/select.def", "[Characters]\nComboGuy\n\n[ExtraStages]\nstages/ring.def\n");
        W("external/script/main.lua", "main()\n");
        W("stages/ring.def", "[StageInfo]\n");
        EnginePath = Path.Combine(Temp("mcp-eng-"), "xray_engine.exe");
        File.WriteAllText(EnginePath, "binary " + PlaybackPreflight.HookName);
        DataDir = Temp("mcp-data-");
        Sandboxes = Temp("mcp-sbx-");
        SettingsJson = JsonSerializer.Serialize(new { schemaVersion = 1, ikemenRoot = Root, xRayEnginePath = EnginePath, xRayStage = "stages/ring.def", xRayDummy = "IkemenGuy" });
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), SettingsJson);
        if (withBroker) Broker = new RuntimeBroker(Path.Combine(Temp("mcp-lock-"), "engine.lock"));
        App = new McpApp(new McpOptions { DataDirectory = DataDir, QueueTimeout = queueTimeout ?? TimeSpan.FromMinutes(15) }, TextWriter.Null, Engine, Broker, Sandboxes);
        App.Server.HandleMessageAsync(Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "mcp-tests", ["version"] = "1" }
        }), CancellationToken.None).GetAwaiter().GetResult();
    }

    public string Root { get; }
    public string EnginePath { get; }
    public string DataDir { get; }
    public string Sandboxes { get; }
    public string SettingsJson { get; }
    public RuntimeBroker? Broker { get; }
    public TestEngine Engine { get; } = new();
    public McpApp App { get; }

    public string Temp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    private int _id;

    public JsonObject Request(string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _id), ["method"] = method, ["params"] = parameters ?? new JsonObject()
    };

    /// <summary>Calls a tool the way a client does (tools/call) and returns its structured result; <paramref name="isError"/> says whether it was an error result.</summary>
    public JsonObject Call(string tool, JsonObject args, out bool isError)
    {
        var response = App.Server.HandleMessageAsync(Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }), CancellationToken.None).GetAwaiter().GetResult()!;
        Assert.Null(response["error"]);
        var result = response["result"]!.AsObject();
        isError = result["isError"]!.GetValue<bool>();
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        var structured = result["structuredContent"]!.AsObject();
        Assert.Equal(JsonNode.Parse(text)!.ToJsonString(), structured.ToJsonString());   // the text block carries the same JSON
        return structured;
    }

    public JsonObject Call(string tool, JsonObject args)
    {
        var r = Call(tool, args, out var isError);
        Assert.False(isError, $"{tool} returned an error: {r.ToJsonString()}");
        return r;
    }

    public JsonObject Error(string tool, JsonObject args)
    {
        var r = Call(tool, args, out var isError);
        Assert.True(isError, $"{tool} should have failed: {r.ToJsonString()}");
        return r;
    }

    /// <summary>Waits for a job to end (get_job with wait_seconds) and returns its final state.</summary>
    public JsonObject Finish(string jobId, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var j = Call("get_job", new JsonObject { ["job_id"] = jobId, ["wait_seconds"] = 5 });
            var status = j["status"]!.GetValue<string>();
            if (status is "completed" or "failed" or "cancelled") return j;
            Assert.True(DateTime.UtcNow < deadline, $"Job {jobId} did not finish: {j.ToJsonString()}");
        }
    }

    public static string Status(JsonObject job) => job["status"]!.GetValue<string>();

    public void Dispose()
    {
        try { App.ShutdownAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); } catch { /* best effort */ }
        Engine.Release();
        _fx.Dispose();
        foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }
}

/// <summary>
/// The test engine: a State Preview gets a forced-state trace, a sequence gets the mock trace of <see cref="Scenario"/>, anything else gets a Play Ability
/// trace (the move starts from its command, hits for 20 and recovers). <see cref="Block"/> holds the run until it is released or cancelled.
/// </summary>
internal sealed class TestEngine : ICancellableEngineRunner
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly object _gate = new();

    public string Scenario { get; set; } = "seq_true";
    /// <summary>What a watched match records (given the engine copy's sha256): by default a knockdown chase.</summary>
    public Func<string, string> WatchTrace { get; set; } = sha => IKEMENLab.Tests.BehaviorTraces.KnockdownChase().Jsonl(sha);
    public string? WatchArguments { get; private set; }
    /// <summary>Hold every run (true) or none (false).</summary>
    public bool Block { get => BlockFromRun != int.MaxValue; set => BlockFromRun = value ? 0 : int.MaxValue; }
    /// <summary>Hold the runs from this run number on (1 = the first run of the test).</summary>
    public int BlockFromRun { get; set; } = int.MaxValue;
    public List<string> Sandboxes { get; } = [];
    public List<string> Plans { get; } = [];
    public SemaphoreSlim Entered { get; } = new(0);
    public int Runs { get { lock (_gate) return Sandboxes.Count; } }

    public void Release() => _release.Set();

    public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);

    public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
    {
        // A watched match (observe mode) has no plan: it gets WatchTrace, stamped with the real hash of the engine copy (as the sandbox's probe config is).
        var planPath = Path.Combine(sandbox.Root, "external", "mods", "xray_plan.lua");
        var lua = File.Exists(planPath) ? File.ReadAllText(planPath) : null;
        int number;
        lock (_gate)
        {
            Sandboxes.Add(sandbox.Root);
            Plans.Add(lua ?? "(watch)");
            if (lua is null) WatchArguments = string.Join(" ", sandbox.Arguments);
            number = Sandboxes.Count;
        }

        Entered.Release();
        if (number >= BlockFromRun)
        {
            WaitHandle.WaitAny([cancel.WaitHandle, _release.WaitHandle], TimeSpan.FromSeconds(30));
            cancel.ThrowIfCancellationRequested();
        }

        if (lua is null)
        {
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sandbox.ExePath)));
            File.WriteAllText(sandbox.TracePath, WatchTrace(sha));
            return new EngineRunResult(0, false, null);
        }

        var fingerprint = Regex.Match(lua, "fingerprint = \"([0-9A-F]+)\"").Groups[1].Value;
        var route = Regex.Match(lua, "route = \"([^\"]+)\"").Groups[1].Value;
        string trace;
        if (lua.Contains("force = true")) trace = Preview(fingerprint, int.Parse(Regex.Match(lua, "toState = (-?\\d+)").Groups[1].Value));
        else if (route.StartsWith("sequence:", StringComparison.Ordinal))
        {
            trace = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"xray_driver_mock_{Scenario}.jsonl"));
            trace = Regex.Replace(trace, "\"planFingerprint\":\"[0-9A-F]*\"", "\"planFingerprint\":\"" + fingerprint + "\"").Replace("sequence:seq-test@v1", route);
        }
        else trace = Ability(fingerprint, int.Parse(Regex.Match(lua, "toState = (-?\\d+)").Groups[1].Value));

        File.WriteAllText(sandbox.TracePath, trace);
        return new EngineRunResult(0, false, null);
    }

    private static string Frame(long f, int p1, bool ctrl, int p2 = 0, string p2Move = "I", double life = 1000, int hit = 0) =>
        $"{{\"type\":\"frame\",\"frame\":{f},\"round\":1,\"p1\":{{\"state\":{p1},\"ctrl\":{(ctrl ? "true" : "false")},\"stateType\":\"S\",\"moveType\":\"{(p1 == 0 ? "I" : "A")}\",\"life\":1000,\"moveHit\":{hit},\"moveContact\":{hit},\"x\":0}}," +
        $"\"p2\":{{\"state\":{p2},\"ctrl\":{(p2 == 0 ? "true" : "false")},\"stateType\":\"S\",\"moveType\":\"{p2Move}\",\"life\":{life},\"moveHit\":0,\"moveContact\":0,\"x\":40}},\"distance\":40,\"distanceSource\":\"derived:p2.x-p1.x\"}}";

    private static string Meta(string fp) => $"{{\"type\":\"meta\",\"frame\":0,\"schema\":\"ikemenlab.xray.trace/0\",\"probeVersion\":\"mcp-test\",\"capabilities\":{{}},\"hooks\":[\"hook:loop\"],\"planFingerprint\":\"{fp}\"}}";

    private static void Move(List<string> l, ref long f, int state)
    {
        for (var i = 0; i < 2; i++) l.Add(Frame(++f, state, false));
        for (var i = 0; i < 10; i++) l.Add(Frame(++f, state, false, 5000, "H", 980, 1));
        for (var i = 0; i < 2; i++) l.Add(Frame(++f, state, false, 0, "I", 980, 1));
        for (var i = 0; i < 30; i++) l.Add(Frame(++f, 0, true, 0, "I", 980));
    }

    private static string Ability(string fp, int to)
    {
        var l = new List<string> { Meta(fp), "{\"type\":\"driver\",\"frame\":0,\"event\":\"plan_start\"}" };
        long f = 0;
        for (var i = 0; i < 3; i++) l.Add(Frame(++f, 0, true));
        l.Add($"{{\"type\":\"input\",\"frame\":{f},\"player\":1,\"keys\":[\"x\"],\"step\":1,\"phase\":\"input\"}}");
        for (var i = 0; i < 2; i++) l.Add(Frame(++f, 0, true));
        l.Add($"{{\"type\":\"input\",\"frame\":{f},\"player\":1,\"keys\":[],\"step\":1,\"phase\":\"input\"}}");
        Move(l, ref f, to);
        l.Add($"{{\"type\":\"driver\",\"frame\":{f},\"event\":\"plan_complete\"}}");
        l.Add($"{{\"type\":\"end\",\"frame\":{f},\"reason\":\"planComplete\"}}");
        return string.Join("\n", l) + "\n";
    }

    private static string Preview(string fp, int to)
    {
        var l = new List<string> { Meta(fp), "{\"type\":\"driver\",\"frame\":0,\"event\":\"plan_start\"}" };
        long f = 0;
        for (var i = 0; i < 25; i++) l.Add(Frame(++f, 0, true));
        l.Add($"{{\"type\":\"driver\",\"frame\":{f},\"event\":\"force_applied\",\"step\":1,\"detail\":\"{to}\"}}");
        l.Add($"{{\"type\":\"driver\",\"frame\":{f},\"event\":\"tail_start\"}}");
        Move(l, ref f, to);
        l.Add($"{{\"type\":\"driver\",\"frame\":{f},\"event\":\"plan_complete\"}}");
        l.Add($"{{\"type\":\"end\",\"frame\":{f},\"reason\":\"planComplete\"}}");
        return string.Join("\n", l) + "\n";
    }
}
