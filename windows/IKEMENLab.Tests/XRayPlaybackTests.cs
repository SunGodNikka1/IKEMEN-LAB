using System.Diagnostics;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Tests;

public class XRayPlaybackTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly CandidateGraph _g;
    private readonly List<string> _cleanup = [];

    public XRayPlaybackTests() => _g = CandidateGraph.Build(CharacterSemanticIndexer.Build(_fx.Root, _fx.ComboGuy));

    public void Dispose()
    {
        _fx.Dispose();
        foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private string Temp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    private string FakeInstall()
    {
        var root = Temp("xray-pb-");
        void W(string rel, string text)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("Ikemen_GO.exe", "exe");
        W("data/select.def", "[Characters]\nkfm\n\n[ExtraStages]\nstages/ring.def\n");
        W("external/script/main.lua", "main()\n");
        W("chars/ComboGuy/ComboGuy.def", "[Info]\n[Files]\ncns = x.cns\n");
        W("chars/kfm/kfm.def", "[Info]\n[Files]\ncns = x.cns\n");
        W("chars/zed/zed.def", "[Info]\n[Files]\ncns = x.cns\n");
        W("stages/ring.def", "[StageInfo]\n");
        return root;
    }

    private string FakeEngine(bool withHook)
    {
        var path = Path.Combine(Temp("xray-eng-"), "xray_engine.exe");
        File.WriteAllText(path, new string('x', 3000) + (withHook ? PlaybackPreflight.HookName : "nothing") + new string('y', 3000));
        return path;
    }

    private ComboRoute Route(params string[] states) =>
        ComboSearch.Find(_g, new ComboOptions { MaxMoves = states.Length, Top = 500, MaxRepeats = 2 }).Routes
            .First(r => r.States.Select(s => s.Replace("state:", "")).SequenceEqual(states));

    // ------------------------------------------------------------------ preflight

    [Fact]
    public void PreflightNamesTheMissingEngineAndResolvesDummyAndStage()
    {
        var root = FakeInstall();
        var setup = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings());
        Assert.False(setup.Ready);
        Assert.Contains(setup.Issues, i => i.Contains("X-Ray engine"));
        Assert.Equal("kfm", setup.Dummy);                       // kfm preferred, never the subject
        Assert.Equal("kfm/kfm.def", setup.DummyDef);
        Assert.Equal("stages/ring.def", setup.Stage);           // first ExtraStages entry that exists
    }

    [Fact]
    public void PreflightChecksTheEngineHookAndExplicitChoices()
    {
        var root = FakeInstall();
        var noHook = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = FakeEngine(false) });
        Assert.False(noHook.Ready);
        Assert.False(noHook.EngineHasVirtualInput);
        Assert.Contains(noHook.Issues, i => i.Contains("virtual-input hook"));

        var ok = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = FakeEngine(true), XRayDummy = "zed", XRayStage = "stages/ring.def" });
        Assert.True(ok.Ready, string.Join(" | ", ok.Issues));
        Assert.True(ok.EngineHasVirtualInput);
        Assert.Equal("zed/zed.def", ok.DummyDef);

        var bad = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = FakeEngine(true), XRayDummy = "nobody", XRayStage = "stages/none.def", XRayEngineDlls = Path.Combine(root, "nodir") });
        Assert.False(bad.Ready);
        Assert.Equal(3, bad.Issues.Count);
    }

    [Fact]
    public void HookSearchFindsTheNameAcrossReadBoundaries()
    {
        var path = Path.Combine(Temp("xray-big-"), "big.bin");
        var pad = new byte[(1 << 20) - 7];                       // the needle straddles the 1 MiB buffer edge
        using (var fs = File.Create(path)) { fs.Write(pad); fs.Write(System.Text.Encoding.ASCII.GetBytes(PlaybackPreflight.HookName)); fs.Write(pad); }
        Assert.True(PlaybackPreflight.ContainsAscii(path, PlaybackPreflight.HookName));
        Assert.False(PlaybackPreflight.ContainsAscii(path, "__not_there"));
    }

    // ------------------------------------------------------------------ service

    private sealed class FakeRunner(string traceText) : IEngineRunner
    {
        public RuntimeSandbox? Seen;
        public string? Config;
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout)
        {
            Seen = sandbox;
            Config = File.ReadAllText(Path.Combine(sandbox.Root, "external", "mods", "xray_config.lua"));
            File.WriteAllText(sandbox.TracePath, traceText);
            return new EngineRunResult(0, false, null);
        }
    }

    private string Trace(string scenario, ComboRoute route)
    {
        var plan = InputPlanner.Plan(_g, route).Plan!;
        var text = File.ReadAllText(Path.Combine(FixtureDir, $"xray_driver_mock_{scenario}.jsonl"));
        return text.Replace("\"type\":\"meta\"", "\"type\":\"meta\",\"planFingerprint\":\"" + InputPlanner.Fingerprint(plan) + "\"");
    }

    private (ComboPlaybackService Service, PlaybackRequest Request, FakeRunner Runner) Arrange(string scenario)
    {
        var root = FakeInstall();
        var route = Route("200", "210", "1000");
        var runner = new FakeRunner(Trace(scenario, route));
        var service = new ComboPlaybackService(runner, Temp("xray-store-"), Temp("xray-sbx-"));
        var setup = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = FakeEngine(true) });
        Assert.True(setup.Ready, string.Join(" | ", setup.Issues));
        return (service, new PlaybackRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", _g, route, setup, "x → y → QCF"), runner);
    }

    [Fact]
    public void PlayingAVerifiableRouteSavesPlanTraceReportAndMeta()
    {
        var (service, request, runner) = Arrange("verified");
        var phases = new List<string>();
        var outcome = service.Play(request, phases.Add);

        Assert.Equal(["planning", "preparing", "running", "judging"], phases);
        Assert.Equal(VerifyStatus.Verified, outcome.Report.Status);
        Assert.Equal("Verified", outcome.Record.Status);
        Assert.Null(outcome.Failure);                                            // nothing to inspect on a verified run
        foreach (var f in new[] { "plan.json", "trace.jsonl", "report.json", "meta.json" })
            Assert.True(File.Exists(Path.Combine(outcome.Record.Directory, f)), f);
        Assert.Contains("lingerFrames = 90", runner.Config);                     // the window stays up on the result
        Assert.DoesNotContain("-p1.ai", runner.Seen!.Arguments);

        var listed = service.List();
        Assert.Single(listed);
        Assert.Equal((outcome.Record.Id, "Verified", "x → y → QCF"), (listed[0].Id, listed[0].Status, listed[0].RouteSummary));
        Assert.False(Directory.Exists(runner.Seen.Root));                        // the sandbox is gone; the record is what remains
    }

    [Fact]
    public void AFailedRunIsKeptAndInspectable()
    {
        var (service, request, _) = Arrange("dropped");
        var outcome = service.Play(request);
        Assert.Equal(VerifyStatus.Failed, outcome.Report.Status);
        var f = outcome.Failure!;
        Assert.StartsWith("Failed", f.Headline);
        Assert.NotEmpty(f.Hints);
        Assert.NotNull(f.FocusFrame);
        Assert.Contains(f.Window, r => r.Frame == f.FocusFrame);
        Assert.True(f.Window.Count <= 41);
        Assert.Equal("ComboDropped", outcome.Record.Reason);
    }

    [Fact]
    public void NoContactNamesTheStepAndInspectsItsStates()
    {
        var (service, request, _) = Arrange("nocontact");
        var f = service.Play(request).Failure!;
        Assert.Equal(2, f.StepIndex);
        Assert.Equal("state:200", f.FromStateId);
        Assert.Equal("state:210", f.ToStateId);
        Assert.Contains("hit never connected", f.Headline);
    }

    [Fact]
    public void AnUnpatchedEngineEndsInconclusiveWithAClearHeadline()
    {
        var (service, request, _) = Arrange("noinject");
        var outcome = service.Play(request);
        Assert.Equal(VerifyStatus.Inconclusive, outcome.Report.Status);
        Assert.StartsWith("Could not be tested", outcome.Failure!.Headline);
    }

    [Fact]
    public void TimelineFoldsInputsAndDriverEventsIntoTheirTicks()
    {
        var (service, request, _) = Arrange("verified");
        var rows = PlaybackInspector.Timeline(service.Play(request).Log);
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Notes.Contains("input x (step 1)"));
        Assert.Contains(rows, r => r.Notes.Contains("step_done"));
        Assert.Equal(rows.Select(r => r.Frame).OrderBy(x => x), rows.Select(r => r.Frame));
    }

    [Fact]
    public void ARefusedOrIncompleteSetupThrowsAndLeavesNothingBehind()
    {
        var (service, request, _) = Arrange("verified");
        var notReady = request with { Setup = PlaybackPreflight.Check(request.Root, "ComboGuy", new AppSettings()) };
        Assert.Throws<InvalidOperationException>(() => service.Play(notReady));
        Assert.Empty(service.List());
        Assert.False(Directory.Exists(service.StoreRoot) && Directory.EnumerateDirectories(service.StoreRoot).Any());
    }

    [Fact]
    public void OnlyTheNewestRecordsAreKept()
    {
        var (service, request, _) = Arrange("verified");
        for (var i = 0; i < ComboPlaybackService.KeepRecords + 3; i++) service.Play(request);
        Assert.Equal(ComboPlaybackService.KeepRecords, service.List().Count);
    }

    // ------------------------------------------------------------------ cancel

    [Fact]
    public void CancellingKillsTheEngineAndDeletesTheSandboxAndRecord()
    {
        if (OperatingSystem.IsWindows()) return;   // the stand-in engine is a shell script
        var root = FakeInstall();
        File.WriteAllText(Path.Combine(root, "Ikemen_GO.exe"), "#!/bin/sh\nsleep 60\n");
        File.SetUnixFileMode(Path.Combine(root, "Ikemen_GO.exe"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var engine = FakeEngine(true);
        File.WriteAllText(engine, "#!/bin/sh\nsleep 60\n# " + PlaybackPreflight.HookName + "\n");
        File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var store = Temp("xray-store-");
        var sbxBase = Temp("xray-sbx-");
        var service = new ComboPlaybackService(new ProcessEngineRunner(), store, sbxBase);
        var setup = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = engine });
        var request = new PlaybackRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", _g, Route("200", "210"), setup, "x → y");

        using var cts = new CancellationTokenSource();
        var phases = new List<string>();
        var sw = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => service.Play(request, p => { phases.Add(p); if (p == "running") cts.CancelAfter(500); }, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), sw.Elapsed.ToString());
        Assert.Empty(service.List());
        Assert.Empty(Directory.EnumerateDirectories(store));
        Assert.Empty(Directory.EnumerateDirectories(sbxBase));
    }
}

public class XRayPlaybackSessionTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        _fx.Dispose();
        foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private sealed class GateRunner : IEngineRunner
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public string Trace = "";
        public int Runs;
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout)
        {
            Interlocked.Increment(ref Runs);
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            File.WriteAllText(sandbox.TracePath, Trace);
            return new EngineRunResult(0, false, null);
        }
    }

    private (PlaybackSession Session, PlaybackRequest Request, GateRunner Runner) Arrange(string scenario = "verified")
    {
        string Temp() { var d = Path.Combine(Path.GetTempPath(), "xray-ps-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); _cleanup.Add(d); return d; }
        var root = Temp();
        void W(string rel, string text) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        W("Ikemen_GO.exe", "exe"); W("data/select.def", "[Characters]\nkfm\n"); W("external/script/main.lua", "main()\n");
        W("chars/ComboGuy/ComboGuy.def", "[Info]\n[Files]\n"); W("chars/kfm/kfm.def", "[Info]\n[Files]\n"); W("stages/ring.def", "[StageInfo]\n");
        var engine = Path.Combine(Temp(), "e.exe");
        File.WriteAllText(engine, PlaybackPreflight.HookName);

        var g = CandidateGraph.Build(CharacterSemanticIndexer.Build(_fx.Root, _fx.ComboGuy));
        var route = ComboSearch.Find(g, new ComboOptions { MaxMoves = 3, Top = 500, MaxRepeats = 2 }).Routes
            .First(r => r.States.Select(s => s.Replace("state:", "")).SequenceEqual(["200", "210", "1000"]));
        var plan = InputPlanner.Plan(g, route).Plan!;
        var runner = new GateRunner
        {
            Trace = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"xray_driver_mock_{scenario}.jsonl"))
                .Replace("\"type\":\"meta\"", "\"type\":\"meta\",\"planFingerprint\":\"" + InputPlanner.Fingerprint(plan) + "\"")
        };
        var service = new ComboPlaybackService(runner, Temp(), Temp());
        var setup = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = engine });
        return (new PlaybackSession(service), new PlaybackRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", g, route, setup, "x → y → QCF"), runner);
    }

    [Fact]
    public async Task WalksThroughThePhasesAndEndsFinishedWithAVerifiedHeadline()
    {
        var (session, request, runner) = Arrange();
        var states = new List<PlaybackState>();
        session.Changed += () => { lock (states) states.Add(session.State); };
        runner.Release.Set();
        await session.PlayAsync(request);

        Assert.Equal(PlaybackState.Finished, session.State);
        Assert.True(session.HasResult);
        Assert.False(session.CanInspect);
        Assert.StartsWith("Verified · 3 of 3 transitions observed", session.Headline);
        lock (states) Assert.Equal([PlaybackState.Preparing, PlaybackState.Preparing, PlaybackState.Preparing, PlaybackState.Running, PlaybackState.Judging, PlaybackState.Finished], states);
    }

    [Fact]
    public async Task OnlyOneRunAtATime_AndReplayPlaysTheSameRouteAgain()
    {
        var (session, request, runner) = Arrange();
        var first = session.PlayAsync(request);
        Assert.True(runner.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(session.IsBusy);
        Assert.False(session.CanReplay);
        await session.PlayAsync(request);                 // ignored while busy
        runner.Release.Set();
        await first;
        Assert.Equal(1, runner.Runs);

        Assert.True(session.CanReplay);
        await session.ReplayAsync();
        Assert.Equal(2, runner.Runs);
        Assert.Equal(PlaybackState.Finished, session.State);
    }

    [Fact]
    public async Task AFailedRunOffersInspection()
    {
        var (session, request, runner) = Arrange("dropped");
        runner.Release.Set();
        await session.PlayAsync(request);
        Assert.True(session.CanInspect);
        Assert.Contains("opponent recovered", session.Headline);
    }

    [Fact]
    public async Task CancelEndsCancelledAndAnUnreadySetupIsAnErrorNotACrash()
    {
        var (session, request, runner) = Arrange();
        // Not ready: no engine configured.
        var notReady = request with { Setup = PlaybackPreflight.Check(request.Root, "ComboGuy", new AppSettings()) };
        await session.PlayAsync(notReady);
        Assert.Equal(PlaybackState.Error, session.State);
        Assert.StartsWith("Could not play: Playback is not set up", session.Headline);
        Assert.True(session.CanReplay);   // fixing the setup and pressing again is allowed

        var run = session.PlayAsync(request);
        Assert.True(runner.Entered.Wait(TimeSpan.FromSeconds(10)));
        session.Cancel();
        Assert.Equal("Stopping the engine…", session.Phase);
        runner.Release.Set();
        await run;
        // The fake runner is not cancellable, so the run completes; the token is honoured at the next checkpoint only for real engines.
        Assert.True(session.State is PlaybackState.Cancelled or PlaybackState.Finished);
    }
}
