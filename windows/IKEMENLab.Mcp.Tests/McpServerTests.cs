using System.Diagnostics;
using System.Text.Json.Nodes;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Mcp.Protocol;
using Xunit;

namespace IKEMENLab.Mcp.Tests;

/// <summary>Phase 4: the MCP protocol surface (handshake, tool list, errors) and the real server process on stdio.</summary>
public class McpServerTests
{
    public static readonly string[] ExpectedTools =
    [
        "inspect_character", "list_abilities", "inspect_ability", "explain_state", "get_source", "list_experiments", "get_experiment_results", "get_runtime_trace",
        "list_behaviors", "inspect_behavior", "why_did_ai_do_this", "watch_match",
        "play_ability", "preview_state", "test_sequence", "create_experiment", "run_experiment", "compare_experiments", "get_job", "cancel_job"
    ];

    /// <summary>The read-only Observe tools (watch_match observes too, but writes a recording).</summary>
    public static readonly string[] ReadOnlyObserve =
        ["inspect_character", "list_abilities", "inspect_ability", "explain_state", "get_source", "list_experiments", "get_experiment_results", "get_runtime_trace",
         "list_behaviors", "inspect_behavior", "why_did_ai_do_this"];

    [Fact]
    public async Task TheHandshakeNegotiatesTheProtocolAndListsExactlyTheObserveAndExperimentTools()
    {
        using var h = new McpHarness();
        var server = h.App.Server;

        var init = (await server.HandleMessageAsync(h.Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "codex", ["version"] = "1" }
        }), CancellationToken.None))!;
        Assert.Equal("2025-03-26", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(McpServer.ServerName, init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.False(init["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
        Assert.Contains("Connected sequence", init["result"]!["instructions"]!.GetValue<string>());
        Assert.Equal("codex", server.ClientName);

        var newest = (await server.HandleMessageAsync(h.Request("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }), CancellationToken.None))!;
        Assert.Equal(McpServer.SupportedVersions[0], newest["result"]!["protocolVersion"]!.GetValue<string>());

        Assert.Null(await server.HandleMessageAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, CancellationToken.None));
        var ping = (await server.HandleMessageAsync(h.Request("ping"), CancellationToken.None))!;
        Assert.Empty(ping["result"]!.AsObject());

        var list = (await server.HandleMessageAsync(h.Request("tools/list"), CancellationToken.None))!["result"]!["tools"]!.AsArray();
        Assert.Equal(ExpectedTools.Order(), list.Select(t => t!["name"]!.GetValue<string>()).Order());
        foreach (var t in list.Select(t => t!.AsObject()))
        {
            Assert.Equal("object", t["inputSchema"]!["type"]!.GetValue<string>());
            Assert.False(t["inputSchema"]!["additionalProperties"]!.GetValue<bool>());
            Assert.False(string.IsNullOrWhiteSpace(t["description"]!.GetValue<string>()));
            Assert.False(t["annotations"]!["destructiveHint"]!.GetValue<bool>());
        }

        // No editing or deployment tool exists in this phase.
        Assert.DoesNotContain(list, t => t!["name"]!.GetValue<string>().Contains("edit", StringComparison.OrdinalIgnoreCase) ||
                                         t!["name"]!.GetValue<string>().Contains("deploy", StringComparison.OrdinalIgnoreCase));
        foreach (var observe in ReadOnlyObserve)
            Assert.True(list.Single(t => t!["name"]!.GetValue<string>() == observe)!["annotations"]!["readOnlyHint"]!.GetValue<bool>());

        var unknownMethod = (await server.HandleMessageAsync(h.Request("resources/list"), CancellationToken.None))!;
        Assert.Equal(McpProtocolException.MethodNotFound, unknownMethod["error"]!["code"]!.GetValue<int>());
        var unknownTool = (await server.HandleMessageAsync(h.Request("tools/call", new JsonObject { ["name"] = "edit_ai" }), CancellationToken.None))!;
        Assert.Equal(McpProtocolException.InvalidParams, unknownTool["error"]!["code"]!.GetValue<int>());
        var parse = await server.HandleLineAsync("{not json", CancellationToken.None);
        Assert.Contains("-32700", parse);

        // Older clients get the JSON as text only; structured content is a 2025-06-18 feature.
        await server.HandleMessageAsync(h.Request("initialize", new JsonObject { ["protocolVersion"] = "2024-11-05" }), CancellationToken.None);
        var old = (await server.HandleMessageAsync(h.Request("tools/call", new JsonObject { ["name"] = "inspect_character", ["arguments"] = new JsonObject { ["character"] = "ComboGuy" } }), CancellationToken.None))!;
        Assert.Null(old["result"]!["structuredContent"]);
        Assert.Contains("Combo Guy", old["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheServerProcessStartsOnStdioAndWritesOnlyProtocolMessagesToStdout()
    {
        using var h = new McpHarness();
        var dll = Path.Combine(AppContext.BaseDirectory, "ikemenlab-mcp.dll");
        Assert.True(File.Exists(dll), "The server assembly is not beside the tests.");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { dll, "--data-dir", h.DataDir }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var lines = new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"process-test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"inspect_character","arguments":{"character":"ComboGuy"}}}"""
        };
        foreach (var l in lines) await p.StandardInput.WriteLineAsync(l);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(30_000), "The server did not exit when its input closed.");
        Assert.Equal(0, p.ExitCode);

        var responses = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(3, responses.Count);                                            // one per request; the notification gets none
        Assert.All(responses, r => Assert.Equal("2.0", r["jsonrpc"]!.GetValue<string>()));
        Assert.Equal(ExpectedTools.Length, responses.Single(r => r["id"]!.GetValue<int>() == 2)["result"]!["tools"]!.AsArray().Count);
        var inspect = responses.Single(r => r["id"]!.GetValue<int>() == 3)["result"]!;
        Assert.False(inspect["isError"]!.GetValue<bool>());
        Assert.Equal("Combo Guy", inspect["structuredContent"]!["character"]!["name"]!.GetValue<string>());
        Assert.Contains("[mcp]", await stderr);                                     // diagnostics go to stderr
    }

    /// <summary>
    /// Regression (found in the real acceptance pass): the engine used to inherit the server's standard streams. Ikemen GO reads its stdin line by line and
    /// runs each line as a command (src/system.go), so under MCP it swallowed a client's request (never answered) and could print into the protocol stream.
    /// The stand-in engine (stdin-engine) does the same — reads stdin from start-up and echoes it — and is launched by the real server process through the
    /// real sandbox and engine runner.
    /// </summary>
    [Fact]
    public async Task TheEngineNeverReadsTheProtocolStdinNorWritesIntoTheProtocolStdout()
    {
        using var h = new McpHarness();
        // The sandbox copies the install's top-level files, so the stand-in's runtime files travel with it; the hook name is appended for the setup check.
        var fake = Path.Combine(h.Root, "Ikemen_GO.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "stdin-engine.exe"), fake, overwrite: true);
        foreach (var f in new[] { "stdin-engine.dll", "stdin-engine.runtimeconfig.json", "stdin-engine.deps.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, f), Path.Combine(h.Root, f), overwrite: true);
        using (var fs = new FileStream(fake, FileMode.Append)) fs.Write(System.Text.Encoding.ASCII.GetBytes(Core.XRay.Playback.PlaybackPreflight.HookName));
        File.WriteAllText(Path.Combine(h.DataDir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1, ikemenRoot = h.Root, xRayEnginePath = fake, xRayStage = "stages/ring.def", xRayDummy = "IkemenGuy"
        }));

        var psi = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { Path.Combine(AppContext.BaseDirectory, "ikemenlab-mcp.dll"), "--data-dir", h.DataDir }) psi.ArgumentList.Add(a);
        psi.Environment[RuntimeBroker.LockPathVariable] = Path.Combine(h.Temp("mcp-lock-"), "engine.lock");   // never the machine-wide lock
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var lines = new System.Collections.Concurrent.BlockingCollection<string>();
        var reader = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync() is { } l) lines.Add(l);
            lines.CompleteAdding();
        });
        var id = 0;
        var seen = new List<JsonObject>();
        async Task<JsonObject> Send(string method, JsonObject args)
        {
            var myId = ++id;
            await p.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = myId, ["method"] = method, ["params"] = args }.ToJsonString());
            await p.StandardInput.FlushAsync();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (!lines.TryTake(out var line, TimeSpan.FromMilliseconds(200))) continue;
                var msg = JsonNode.Parse(line)!.AsObject();                          // every stdout line must be a protocol message
                Assert.Equal("2.0", msg["jsonrpc"]!.GetValue<string>());
                seen.Add(msg);
                if (msg["id"]?.GetValue<int>() == myId) return msg;
            }

            throw new Xunit.Sdk.XunitException($"Request {myId} ({method}) was never answered. stderr: {(stderr.IsCompleted ? stderr.Result : "")}");
        }

        JsonObject Tool(string name, JsonObject args) => new() { ["name"] = name, ["arguments"] = args };
        JsonObject Body(JsonObject response) => response["result"]!["structuredContent"]!.AsObject();

        await Send("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "stdin-test" } });
        var job = Body(await Send("tools/call", Tool("play_ability", new JsonObject { ["character"] = "ComboGuy", ["ability"] = "200" })))["jobId"]!.GetValue<string>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var state = Body(await Send("tools/call", Tool("get_job", new JsonObject { ["job_id"] = job })));
            if (state["phase"]?.GetValue<string>()?.Contains("Playing", StringComparison.Ordinal) == true) break;   // the stand-in engine is running
            Assert.True(DateTime.UtcNow < deadline, "The stand-in engine never started: " + state.ToJsonString());
            await Task.Delay(200);
        }

        await Task.Delay(1000);                                                       // give it time to read stdin, if it could
        for (var i = 0; i < 8; i++) Assert.Equal("running", Body(await Send("tools/call", Tool("get_job", new JsonObject { ["job_id"] = job })))["status"]!.GetValue<string>());
        Assert.True(Body(await Send("tools/call", Tool("cancel_job", new JsonObject { ["job_id"] = job })))["accepted"]!.GetValue<bool>());
        Assert.Equal("cancelled", Body(await Send("tools/call", Tool("get_job", new JsonObject { ["job_id"] = job, ["wait_seconds"] = 10 })))["status"]!.GetValue<string>());

        p.StandardInput.Close();
        Assert.True(p.WaitForExit(30_000));
        await reader;
        Assert.Equal(0, p.ExitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(h.DataDir, "runtime-sandboxes")));
    }
}
