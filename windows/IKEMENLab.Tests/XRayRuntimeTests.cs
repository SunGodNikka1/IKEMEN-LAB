using System.Text;
using IKEMENLab.Cli;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Runtime;
using Xunit;

namespace IKEMENLab.Tests;

public class XRayRuntimeTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        _fx.Dispose();
        foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private static string MockTracePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "xray_probe_mock_trace.jsonl");

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "xray-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    // ------------------------------------------------------------------ the trace format, using a trace the real probe emitted (against a mock engine)

    [Fact]
    public void ProbeEmittedTraceParsesCleanly()
    {
        var log = TraceReader.ReadFile(MockTracePath);
        Assert.Empty(log.Issues);
        Assert.Equal("ikemenlab.xray.trace/0", log.Meta!.Schema);
        Assert.Equal("0.1-spike", log.Meta.ProbeVersion);
        Assert.True(log.Meta.Capabilities["p1.state"]);
        Assert.False(log.Meta.Capabilities["p1.prevState"]);   // reported as unsupported, not faked
        Assert.Contains("hook:loop", log.Meta.HooksRegistered);

        var frames = log.Frames.ToList();
        Assert.Equal(40, frames.Count);
        Assert.Equal(frames.Select(f => f.Frame).OrderBy(x => x), frames.Select(f => f.Frame));   // monotonic identity
        Assert.All(frames, f => Assert.Null(f.P1.PrevState));                                     // unsupported => null
        Assert.All(frames, f => Assert.Null(f.P1.VelX));
        Assert.Equal(1000, frames[0].P1.Life);
        Assert.Equal("S", frames[0].P1.StateType);
        Assert.True(frames[0].P1.Ctrl);
        Assert.Equal(120, frames[0].Distance);
    }

    [Fact]
    public void ProbeTraceCarriesRawStateAndLifeFactsNotInterpretations()
    {
        var log = TraceReader.ReadFile(MockTracePath);
        var changes = log.Events.OfType<StateChangeEvent>().Where(c => c.Player == 1).Select(c => (c.From, c.To)).ToList();
        Assert.Equal([(0, 200), (200, 210), (210, 0)], changes);
        var life = Assert.Single(log.Events.OfType<LifeChangeEvent>());
        Assert.Equal((2, 1000.0, 920.0), (life.Player, life.From, life.To));
        Assert.Empty(log.Events.OfType<HitEvent>());   // the probe reports facts; "hit" is a later interpretation
        Assert.IsType<EndEvent>(log.Events[^1]);
        Assert.Contains(log.Events, e => e is UnknownEvent { Type: "probe_loaded" });
    }

    [Fact]
    public void TruncatedFinalLineAndStaleBytesAreIgnoredNotFatal()
    {
        var good = "{\"type\":\"meta\",\"frame\":0}\n{\"type\":\"frame\",\"frame\":1,\"p1\":{\"state\":0},\"p2\":{}}\n";
        var log = TraceReader.Read(good + "{\"type\":\"frame\",\"frame\":2,\"p1\":{\"sta");
        Assert.Single(log.Frames);
        Assert.Contains(log.Issues, i => i.Message.Contains("incomplete final line"));

        // Stale bytes from an earlier, longer file after the last newline (the failure mode of rewriting a file in place).
        var stale = good + "\0\0\0ate\":1,\"p2\":{}}\n\0\0";
        var log2 = TraceReader.Read(stale);
        Assert.Single(log2.Frames);
        Assert.NotEmpty(log2.Issues);
    }

    [Fact]
    public void FramesMustNotGoBackwards_AndEventsNeedAFrame()
    {
        var text = "{\"type\":\"meta\",\"frame\":0}\n" +
                   "{\"type\":\"frame\",\"frame\":5,\"p1\":{},\"p2\":{}}\n" +
                   "{\"type\":\"frame\",\"frame\":3,\"p1\":{},\"p2\":{}}\n" +
                   "{\"type\":\"state_change\",\"player\":1,\"from\":0,\"to\":200}\n" +
                   "{\"type\":\"frame\",\"frame\":6,\"p1\":{},\"p2\":{}}\n";
        var log = TraceReader.Read(text);
        Assert.Equal([5L, 6L], log.Frames.Select(f => f.Frame));
        Assert.Contains(log.Issues, i => i.Message.Contains("goes back"));
        Assert.Contains(log.Issues, i => i.Message.Contains("no numeric frame"));
    }

    [Fact]
    public void OddInputIsToleratedAndKept()
    {
        var text = "\n{\"type\":\"frame\",\"frame\":1,\"p1\":{\"ctrl\":1,\"state\":\"x\",\"life\":12.5},\"p2\":7}\n" +
                   "{\"type\":\"future_thing\",\"frame\":2,\"a\":1}\n[1,2]\n{\"nope\":1}\nnot json\n" +
                   "{\"type\":\"meta\",\"frame\":0}\n{\"type\":\"meta\",\"frame\":0}\n";
        var log = TraceReader.Read(text);
        var f = Assert.Single(log.Frames);
        Assert.True(f.P1.Ctrl);                 // engines may report ctrl as 0/1
        Assert.Null(f.P1.State);                // a string where a number belongs is not coerced
        Assert.Equal(12.5, f.P1.Life);
        Assert.Null(f.P2.State);                // p2 was not an object
        Assert.Contains(log.Events, e => e is UnknownEvent { Type: "future_thing" });
        Assert.Contains(log.Issues, i => i.Message.Contains("second meta"));
        Assert.True(log.Issues.Count >= 4);
        Assert.Null(TraceReader.Read("{\"type\":\"frame\",\"frame\":1}").Meta);
        Assert.Contains(TraceReader.Read("{\"type\":\"frame\",\"frame\":1}").Issues, i => i.Message.Contains("no meta"));
    }

    // ------------------------------------------------------------------ runtime -> static association

    [Fact]
    public void RuntimeStatesAttachToTheirStaticStateObjects()
    {
        var index = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine);
        // A trace of a match with the Valentine fixture: states 0 (common), 200 and 210 (defined by the character), 4242 (defined nowhere).
        var trace = new StringBuilder("{\"type\":\"meta\",\"frame\":0,\"schema\":\"ikemenlab.xray.trace/0\"}\n");
        var sequence = new[] { (0, 0), (1, 0), (2, 200), (3, 200), (4, 210), (5, 4242), (6, 0) };
        foreach (var (frame, state) in sequence)
            trace.Append($"{{\"type\":\"frame\",\"frame\":{frame + 1},\"p1\":{{\"state\":{state},\"anim\":{(state == 200 ? 200 : 0)}}},\"p2\":{{\"state\":5000}}}}\n");
        trace.Append("{\"type\":\"state_change\",\"frame\":8,\"player\":1,\"from\":0,\"to\":3000}\n");

        var evidence = RuntimeLink.Associate(index, TraceReader.Read(trace.ToString()));

        var s200 = evidence.States.Single(s => s.StateNo == 200);
        Assert.Equal("state:200", s200.ObjectId);
        Assert.Equal(2, s200.Frames);
        Assert.Equal(3, s200.FirstFrame);

        var s0 = evidence.States.Single(s => s.StateNo == 0);
        Assert.Equal("state:0", s0.ObjectId);
        Assert.True(s0.IsCommonState);
        Assert.Equal(3, s0.Frames);

        Assert.Equal("state:210", evidence.States.Single(s => s.StateNo == 210).ObjectId);
        Assert.Null(evidence.States.Single(s => s.StateNo == 4242).ObjectId);           // not defined in the static index
        Assert.Equal("state:3000", evidence.States.Single(s => s.StateNo == 3000).ObjectId);   // seen only through a state_change
        Assert.All(evidence.States, s => Assert.Equal(1, s.Player));                     // the dummy's states are not linked
        Assert.DoesNotContain(evidence.States, s => s.StateNo == 5000);
        Assert.Equal("anim:200", evidence.Animations.Single(a => a.AnimNo == 200).ObjectId);
    }

    [Fact]
    public void RuntimeLinksNeverPromoteStaticRelationshipsToRuntimeVerified()
    {
        var index = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine);
        var before = index.Relationships.Select(r => r.Confidence).ToList();
        RuntimeLink.Associate(index, TraceReader.ReadFile(MockTracePath));
        Assert.Equal(before, index.Relationships.Select(r => r.Confidence));
        Assert.DoesNotContain(index.Relationships, r => r.Confidence == Confidence.RuntimeVerified);
    }

    // ------------------------------------------------------------------ the probe file

    [Fact]
    public void ProbeIsAppendOnlyAndReportsFactsNotJudgements()
    {
        var lua = RuntimeProbe.Source;
        var code = string.Join("\n", lua.Split('\n').Where(l => !l.TrimStart().StartsWith("--")));   // judgement words may appear in comments that disclaim them
        Assert.Contains("io.open(cfg.trace, \"a\")", lua);
        Assert.DoesNotContain("io.open(cfg.trace, \"w\"", lua);
        Assert.DoesNotContain("\"wb\"", lua);
        Assert.Contains("ikemenlab.xray.trace/0", lua);
        Assert.Contains("pcall", lua);
        foreach (var judgement in new[] { "anti-air", "antiair", "combo_valid", "true_combo", "\"throw\"" })
            Assert.DoesNotContain(judgement, code, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the sandbox

    private string MakeFakeInstall()
    {
        var root = TempDir();
        void W(string rel, string text)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("Ikemen_GO.exe", "exe");
        W("OpenAL32.dll", "dll");
        W("debug.log", "log");
        W("data/select.def", "[Characters]\nkfm\nuser1\nuser2\n\n[ExtraStages]\nstages/old.def\n\n[Options]\narcade.maxmatches = 6\n");
        W("data/system.def", "motif");
        W("external/script/main.lua", "-- main\nmain()\n");
        W("save/config.ini", "[Config]\nMotif = data/system.def\n");
        W("save/stats.json", "{}");
        W("chars/kfm/kfm.def", "[Info]\nname = kfm\n[Files]\ncns = kfm.cns\n");
        W("chars/kfm/kfm.cns", "x");
        W("chars/hero/hero.def", "[Info]\nname = hero\n[Files]\ncns = hero.cns\n");
        W("chars/hero/hero.cns", "x");
        W("chars/other/other.def", "[Info]\n");
        W("stages/ring.def", "[StageInfo]\n");
        W("stages/ring.sff", "s");
        W("stages/unrelated.def", "[StageInfo]\n");
        return root;
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void SandboxHoldsOneSubjectOneDummyOneStageAndTheProbe_AndLeavesTheSourceUntouched()
    {
        var source = MakeFakeInstall();
        var before = Snapshot(source);
        using var sb = RuntimeSandbox.Create(new SandboxRequest(source, "hero", "hero/hero.def", "kfm", "kfm/kfm.def", "stages/ring.def", 120, TempDir()));

        Assert.Equal(before, Snapshot(source));                                    // the real install is byte-identical
        Assert.True(File.Exists(sb.ExePath));
        Assert.True(File.Exists(Path.Combine(sb.Root, "OpenAL32.dll")));
        Assert.False(File.Exists(Path.Combine(sb.Root, "debug.log")));
        Assert.True(File.Exists(Path.Combine(sb.Root, "chars", "hero", "hero.def")));
        Assert.True(File.Exists(Path.Combine(sb.Root, "chars", "kfm", "kfm.def")));
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "chars", "other")));   // no other characters
        Assert.True(File.Exists(Path.Combine(sb.Root, "stages", "ring.sff")));
        Assert.False(File.Exists(Path.Combine(sb.Root, "stages", "unrelated.def")));
        Assert.True(File.Exists(Path.Combine(sb.Root, "save", "config.ini")));
        Assert.False(File.Exists(Path.Combine(sb.Root, "save", "stats.json")));

        var select = File.ReadAllText(Path.Combine(sb.Root, "data", "select.def"));
        Assert.Contains("[Characters]\nhero\nkfm\n", select);
        Assert.DoesNotContain("user1", select);
        Assert.Contains("[ExtraStages]\nstages/ring.def\n", select);
        Assert.Contains("arcade.maxmatches = 6", select);                          // unrelated sections survive
        Assert.Contains("stages/old.def", File.ReadAllText(Path.Combine(source, "data", "select.def")));

        Assert.Contains("ikemenlab.xray.trace/0", File.ReadAllText(Path.Combine(sb.Root, "external", "mods", "xray_probe.lua")));
        var config = File.ReadAllText(Path.Combine(sb.Root, "external", "mods", "xray_config.lua"));
        Assert.Contains("maxFrames = 120", config);
        Assert.Contains(sb.TracePath.Replace('\\', '/'), config);
        Assert.StartsWith("pcall(dofile, \"external/mods/xray_probe.lua\")", File.ReadAllText(Path.Combine(sb.Root, "external", "script", "main.lua")));
        Assert.Equal("-- main\nmain()\n", File.ReadAllText(Path.Combine(source, "external", "script", "main.lua")));
        Assert.Equal(["-p1", "hero", "-p2", "kfm"], sb.Arguments.Take(4));
    }

    [Fact]
    public void SandboxRefusesToLiveInsideTheInstallAndDeleteRefusesNonSandboxes()
    {
        var source = MakeFakeInstall();
        Assert.Throws<InvalidOperationException>(() =>
            RuntimeSandbox.Create(new SandboxRequest(source, "hero", "hero/hero.def", "kfm", "kfm/kfm.def", "stages/ring.def", 10, Path.Combine(source, "sandboxes"))));
        Assert.False(RuntimeSandbox.Delete(source));                 // a real install has no marker
        Assert.True(Directory.Exists(source));

        var sb = RuntimeSandbox.Create(new SandboxRequest(source, "hero", "hero/hero.def", "kfm", "kfm/kfm.def", "stages/ring.def", 10, TempDir()));
        var root = sb.Root;
        sb.Dispose();
        Assert.False(Directory.Exists(root));
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public void MissingStageIsReportedNotFatal()
    {
        var source = MakeFakeInstall();
        using var sb = RuntimeSandbox.Create(new SandboxRequest(source, "hero", "hero/hero.def", "kfm", "kfm/kfm.def", "stages/nope.def", 10, TempDir()));
        Assert.Contains(sb.Notes, n => n.Contains("nope.def"));
    }

    [Fact]
    public void ReplaceSectionHandlesMissingAndCrlfSections()
    {
        Assert.Equal("[A]\nx\n", RuntimeSandbox.ReplaceSection("[A]\r\nold\r\n", "A", "x\n"));
        Assert.Contains("[Characters]\nk\n", RuntimeSandbox.ReplaceSection("[Options]\na = 1\n", "Characters", "k\n"));
    }

    // ------------------------------------------------------------------ CLI (prepare / report / clean)

    [Fact]
    public void CliPreparesReportsAndCleansASandbox()
    {
        var source = MakeFakeInstall();
        var sandboxBase = TempDir();
        var prep = new StringWriter();
        Assert.Equal(0, CliApp.Run(["xray", "runtime-prepare", "--root", source, "--subject", "hero", "--dummy", "kfm", "--stage", "stages/ring.def", "--frames", "50", "--out", sandboxBase], prep, new StringWriter()));
        using var doc = System.Text.Json.JsonDocument.Parse(prep.ToString());
        var sandbox = doc.RootElement.GetProperty("sandbox").GetString()!;
        Assert.True(Directory.Exists(sandbox));
        Assert.EndsWith("xray_trace.jsonl", doc.RootElement.GetProperty("trace").GetString());
        Assert.Equal("-p1", doc.RootElement.GetProperty("arguments")[0].GetString());

        // Report on a trace for the Valentine fixture character.
        var report = new StringWriter();
        var code = CliApp.Run(["xray", "runtime-report", Path.Combine(_fx.Root, "chars", "Valentine"), "--trace", MockTracePath], report, new StringWriter());
        Assert.Equal(0, code);
        using var rep = System.Text.Json.JsonDocument.Parse(report.ToString());
        Assert.True(rep.RootElement.GetProperty("validTrace").GetBoolean());
        Assert.True(rep.RootElement.GetProperty("associatedAtLeastOneState").GetBoolean());   // state 0 / 200 / 210 exist in the fixture
        Assert.Contains(rep.RootElement.GetProperty("meta").GetProperty("unsupported").EnumerateArray(), e => e.GetString() == "p1.prevState");
        Assert.Contains(rep.RootElement.GetProperty("states").EnumerateArray(), s => s.GetProperty("stateNo").GetInt32() == 200 && s.GetProperty("object").GetString() == "state:200");

        Assert.Equal(0, CliApp.Run(["xray", "runtime-clean", sandbox], new StringWriter(), new StringWriter()));
        Assert.False(Directory.Exists(sandbox));
        Assert.Equal(3, CliApp.Run(["xray", "runtime-clean", source], new StringWriter(), new StringWriter()));
        Assert.True(Directory.Exists(source));
        Assert.Equal(2, CliApp.Run(["xray", "runtime-prepare"], new StringWriter(), new StringWriter()));
    }
}
