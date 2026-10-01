using System.Text.Json;
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
        text = text.Replace("\"planFingerprint\":null,", string.Empty);   // newer probes write the key (null without a host-supplied fingerprint)
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
        Assert.Contains("did not connect", f.Headline);                                  // the unmet prerequisite, named from the evidence
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

    // ------------------------------------------------------------------ cancellation boundary

    private sealed class CancellingRunner(CancellationTokenSource cts, string trace) : IEngineRunner
    {
        public int Runs;
        public RuntimeSandbox? Seen;
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout)
        {
            Runs++;
            Seen = sandbox;
            File.WriteAllText(sandbox.TracePath, trace);
            cts.Cancel();                 // the cancel arrives just as the engine exits
            return new EngineRunResult(0, false, null);
        }
    }

    [Fact]
    public void ACancelRequestedAsTheEngineExitsProducesNoResultAndNoRecord()
    {
        var (service, request, _) = Arrange("verified");
        using var cts = new CancellationTokenSource();
        var runner = new CancellingRunner(cts, Trace("verified", request.Route));
        var racing = new ComboPlaybackService(runner, service.StoreRoot, Temp("xray-sbx-"));
        Assert.Throws<OperationCanceledException>(() => racing.Play(request, null, cts.Token));
        Assert.Equal(1, runner.Runs);
        Assert.Empty(racing.List());
        Assert.Empty(Directory.EnumerateDirectories(service.StoreRoot));
        Assert.False(Directory.Exists(runner.Seen!.Root));
    }

    [Fact]
    public void ACancelDuringJudgingStillCancelsBecauseNothingIsCommittedYet()
    {
        var (service, request, runner) = Arrange("verified");
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => service.Play(request, p => { if (p == "judging") cts.Cancel(); }, cts.Token));
        Assert.Empty(service.List());
        Assert.False(Directory.Exists(runner.Seen!.Root));
    }

    [Fact]
    public void ACancelAfterTheResultIsCommittedDoesNotTakeItBack()
    {
        var (service, request, _) = Arrange("verified");
        using var cts = new CancellationTokenSource();
        var outcome = service.Play(request, null, cts.Token);
        cts.Cancel();
        Assert.Single(service.List());
        Assert.True(File.Exists(outcome.Record.ReportPath) && File.Exists(Path.Combine(outcome.Record.Directory, "meta.json")));
    }

    [Fact]
    public void ACancelDuringPreparationNeverLaunchesTheEngineAndLeavesNothing()
    {
        var (_, request, _) = Arrange("verified");
        var runner = new FakeRunner("");
        var store = Temp("xray-store-");
        var sbxBase = Temp("xray-sbx-");
        var service = new ComboPlaybackService(runner, store, sbxBase);
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => service.Play(request, p => { if (p == "preparing") cts.Cancel(); }, cts.Token));
        Assert.Null(runner.Seen);                                  // the engine was never started
        Assert.Empty(Directory.EnumerateDirectories(store));
        Assert.Empty(Directory.EnumerateDirectories(sbxBase));     // the half-built sandbox is gone
    }

    // ------------------------------------------------------------------ atomic cancel / commit

    private (ComboPlaybackService Service, PlaybackRequest Request, string Store, string Sbx) ArrangeWithSeam(Action<string> seam)
    {
        var (_, request, _) = Arrange("verified");
        var runner = new FakeRunner(Trace("verified", request.Route));
        var store = Temp("xray-store-");
        var sbx = Temp("xray-sbx-");
        return (new ComboPlaybackService(runner, store, sbx) { CommitSeam = seam }, request, store, sbx);
    }

    [Fact]
    public void ACancelInTheWindowBetweenTheLastCheckAndTheCommitIsAcceptedAndNothingIsPublished()
    {
        using var gate = new PlaybackCancellation();
        var accepted = false;
        var (service, request, store, sbx) = ArrangeWithSeam(point =>
        {
            if (point == "before-commit") accepted = gate.Cancel();   // exactly the previously racy moment: all checks passed, commit not yet taken
        });

        Assert.Throws<OperationCanceledException>(() => service.Play(request, null, gate));
        Assert.True(accepted);                                       // the canceller was told yes ...
        Assert.Empty(service.List());                                // ... and there is no record (both cannot win)
        Assert.Empty(Directory.EnumerateDirectories(store));
        Assert.Empty(Directory.EnumerateDirectories(sbx));
        Assert.True(gate.IsCancelled && !gate.IsCommitted);
    }

    [Fact]
    public void ACancelRightAfterTheCommitIsRefusedAndTheRecordSurvives()
    {
        using var gate = new PlaybackCancellation();
        bool? accepted = null;
        var (service, request, _, _) = ArrangeWithSeam(point =>
        {
            if (point == "after-commit") accepted = gate.Cancel();
        });

        var outcome = service.Play(request, null, gate);
        Assert.False(accepted);                                      // too late, and the canceller is told so
        Assert.Single(service.List());
        Assert.True(File.Exists(Path.Combine(outcome.Record.Directory, "meta.json")));
        Assert.False(File.Exists(Path.Combine(outcome.Record.Directory, "meta.json.tmp")));
        Assert.True(gate.IsCommitted && !gate.IsCancelled);
    }

    [Fact]
    public void ARawTokenCancelInTheWindowIsRoutedThroughTheSameGate()
    {
        using var cts = new CancellationTokenSource();
        var (service, request, store, _) = ArrangeWithSeam(point => { if (point == "before-commit") cts.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => service.Play(request, null, cts.Token));
        Assert.Empty(Directory.EnumerateDirectories(store));
    }

    [Fact]
    public void CancelAndCommitNeverBothSucceedUnderContention()
    {
        for (var i = 0; i < 3000; i++)
        {
            using var gate = new PlaybackCancellation();
            var published = 0;
            bool committed = false, cancelAccepted = false;
            using var start = new ManualResetEventSlim();
            var t1 = Task.Run(() => { start.Wait(); committed = gate.TryCommit(() => published++); });
            var t2 = Task.Run(() => { start.Wait(); cancelAccepted = gate.Cancel(); });
            start.Set();
            Task.WaitAll(t1, t2);
            Assert.NotEqual(committed, cancelAccepted);              // exactly one wins
            Assert.Equal(committed ? 1 : 0, published);              // and the result is published only if the commit won
        }
    }

    [Fact]
    public void AFailingPublishLeavesTheRunPendingAndRethrows()
    {
        using var gate = new PlaybackCancellation();
        Assert.Throws<IOException>(() => gate.TryCommit(() => throw new IOException("disk")));
        Assert.False(gate.IsCommitted);
        Assert.True(gate.Cancel());
    }

    // ------------------------------------------------------------------ cleanup failure during preparation

    [Fact]
    public void AFailedPreparationWhoseSandboxCannotBeDeletedReportsTheLeftover()
    {
        var (_, request, _) = Arrange("verified");
        var runner = new FakeRunner("");
        var sbx = Temp("xray-sbx-");
        var reported = new List<(string Path, string? Why)>();
        var service = new ComboPlaybackService(runner, Temp("xray-store-"), sbx) { SandboxDeleter = _ => (false, "deliberately locked") };
        service.CleanupFailed += (p, w) => reported.Add((p, w));

        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => service.Play(request, p => { if (p == "preparing") cts.Cancel(); }, cts.Token));

        Assert.Null(runner.Seen);                                   // preparation was cancelled: the engine never started
        var leftover = Assert.Single(reported);                     // ... and the failed deletion was reported, not swallowed
        Assert.StartsWith(sbx, leftover.Path);
        Assert.Equal("deliberately locked", leftover.Why);
        Assert.True(Directory.Exists(leftover.Path));               // the injected deleter really left it behind
        Assert.True(RuntimeSandbox.Delete(leftover.Path));          // tidy up with the real deleter
    }

    [Fact]
    public void AFailedPreparationThatCleansUpReportsNothing()
    {
        var (_, request, _) = Arrange("verified");
        var sbx = Temp("xray-sbx-");
        var reported = 0;
        var service = new ComboPlaybackService(new FakeRunner(""), Temp("xray-store-"), sbx);
        service.CleanupFailed += (_, _) => reported++;
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => service.Play(request, p => { if (p == "preparing") cts.Cancel(); }, cts.Token));
        Assert.Equal(0, reported);
        Assert.Empty(Directory.EnumerateDirectories(sbx));
    }

    [Fact]
    public void APostRunCleanupFailureIsReportedThroughTheSamePath()
    {
        var (_, request, _) = Arrange("verified");
        var sbx = Temp("xray-sbx-");
        var reported = new List<string?>();
        var service = new ComboPlaybackService(new FakeRunner(Trace("verified", request.Route)), Temp("xray-store-"), sbx) { SandboxDeleter = _ => (false, "still open") };
        service.CleanupFailed += (_, w) => reported.Add(w);
        service.Play(request);                                       // the verdict stands; only the cleanup problem is reported
        Assert.Equal(["still open"], reported);
        foreach (var d in Directory.EnumerateDirectories(sbx)) RuntimeSandbox.Delete(d);
    }

    // ------------------------------------------------------------------ trace provenance, DLL convention, linger

    [Fact]
    public void DistanceIsShownWithItsProvenanceAndPositionsAreSeparateRawFacts()
    {
        var log = TraceReader.Read("{\"type\":\"frame\",\"frame\":1,\"distance\":40,\"distanceSource\":\"derived:p2.x-p1.x\",\"p1\":{\"x\":10},\"p2\":{\"x\":50}}\n" +
                                   "{\"type\":\"frame\",\"frame\":2,\"distance\":12,\"distanceSource\":\"engine-trigger\",\"p1\":{},\"p2\":{}}\n" +
                                   "{\"type\":\"frame\",\"frame\":3,\"distance\":7,\"p1\":{},\"p2\":{}}\n");
        var rows = PlaybackInspector.Timeline(log);
        Assert.Equal((10.0, 50.0), (rows[0].P1X, rows[0].P2X));
        Assert.Equal("40 (derived: p2.x − p1.x)", rows[0].DistanceText);     // never presented as an engine reading
        Assert.Equal("12 (engine trigger)", rows[1].DistanceText);
        Assert.Equal("7 (source not recorded)", rows[2].DistanceText);
        Assert.Equal(string.Empty, new TraceRow(1, null, null, null, null, null, null, null, null, null, null, null, "").DistanceText);
    }

    [Fact]
    public void RuntimeDllsDefaultToTheEnginesOwnFolderUnlessChosen()
    {
        var root = FakeInstall();
        var engine = FakeEngine(true);
        Assert.Null(PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = engine }).EngineDlls);   // no DLLs beside it yet
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine)!, "SDL2.dll"), "dll");
        var auto = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = engine });
        Assert.Equal(Path.GetDirectoryName(engine), auto.EngineDlls);
        Assert.Contains(auto.Notes, n => n.Contains("Runtime DLLs"));

        var other = Temp("xray-dll-");
        Assert.Equal(other, PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = engine, XRayEngineDlls = other }).EngineDlls);
    }

    [Fact]
    public void ALingerTraceRecordsTheHoldAndStillVerifies()
    {
        var (service, request, _) = Arrange("linger");
        var outcome = service.Play(request);
        Assert.True(outcome.Report.Status == VerifyStatus.Verified, outcome.Report.Reason + " / " + string.Join(" | ", outcome.Report.Notes) + " / " + outcome.Report.Continuity.Detail);        // linger events after the end do not disturb the verdict
        var start = outcome.Log.Events.OfType<DriverEvent>().Single(d => d.Kind == "linger_start");
        var end = outcome.Log.Events.OfType<DriverEvent>().Single(d => d.Kind == "linger_end");
        Assert.Equal(ComboPlaybackService.LingerFrames, end.Frame - start.Frame);   // acceptance compares this to what was seen on screen
        Assert.Contains("probeTick=", start.Detail);
        Assert.DoesNotContain("clock", start.Detail!);                                 // os.clock() is CPU time, never evidence of what was seen
        Assert.DoesNotContain(outcome.Log.Frames, f => f.Frame > start.Frame);       // the hold is silent: no input, no new samples
    }

    [Fact]
    public void TheLingerIsPassedToTheSandboxAsAHoldNotAnImmediateExit()
    {
        var (service, request, runner) = Arrange("verified");
        service.Play(request);
        Assert.Contains("lingerFrames = 90", runner.Config);
        var probe = RuntimeProbe.Source;
        Assert.Contains("linger_start", probe);
        Assert.Contains("linger_end", probe);
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

    private sealed class WaitingRunner : ICancellableEngineRunner
    {
        public readonly ManualResetEventSlim Entered = new();
        public RuntimeSandbox? Seen;
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
        {
            Seen = sandbox;
            Entered.Set();
            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
            cancel.ThrowIfCancellationRequested();
            return new EngineRunResult(0, false, null);
        }
    }

    private string _store = "", _sbx = "";

    private (PlaybackSession Session, PlaybackRequest Request, GateRunner Runner) Arrange(string scenario = "verified", Func<IEngineRunner>? custom = null)
    {
        string Temp() { var d = Path.Combine(Path.GetTempPath(), "xray-ps-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); _cleanup.Add(d); return d; }
        var root = Temp();
        void W(string rel, string text) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        W("Ikemen_GO.exe", "exe"); W("data/select.def", "[Characters]\nkfm\n"); W("external/script/main.lua", "main()\n");
        W("chars/ComboGuy/ComboGuy.def", "[Info]\n[Files]\n"); W("chars/kfm/kfm.def", "[Info]\n[Files]\n"); W("stages/ring.def", "[StageInfo]\n");
        var enginePath = Path.Combine(Temp(), "e.exe");
        File.WriteAllText(enginePath, PlaybackPreflight.HookName);

        var g = CandidateGraph.Build(CharacterSemanticIndexer.Build(_fx.Root, _fx.ComboGuy));
        var route = ComboSearch.Find(g, new ComboOptions { MaxMoves = 3, Top = 500, MaxRepeats = 2 }).Routes
            .First(r => r.States.Select(s => s.Replace("state:", "")).SequenceEqual(["200", "210", "1000"]));
        var plan = InputPlanner.Plan(g, route).Plan!;
        var runner = new GateRunner
        {
            Trace = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"xray_driver_mock_{scenario}.jsonl"))
                .Replace("\"type\":\"meta\"", "\"type\":\"meta\",\"planFingerprint\":\"" + InputPlanner.Fingerprint(plan) + "\"")
        };
        _store = Temp(); _sbx = Temp();
        var service = new ComboPlaybackService(custom?.Invoke() ?? runner, _store, _sbx);
        var setup = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = enginePath });
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

    [Fact]
    public async Task ResultsAndResultActionsBelongOnlyToTheRouteThatWasPlayed()
    {
        var (session, request, runner) = Arrange("dropped");
        runner.Release.Set();
        await session.PlayAsync(request);
        var a = request.Route.Key;
        const string b = "some-other-route";

        Assert.True(session.HasResultFor(a) && session.CanInspectFor(a) && session.CanReplayFor(a));
        Assert.Contains("opponent recovered", session.HeadlineFor(a));

        // Route B is selected: A's banner, Inspect and Replay must not appear against it.
        Assert.False(session.HasResultFor(b));
        Assert.False(session.CanInspectFor(b));
        Assert.False(session.CanReplayFor(b));
        Assert.Equal(string.Empty, session.HeadlineFor(b));
        Assert.False(session.IsFor(null));
        Assert.Equal(a, session.Outcome!.Report.RouteKey);   // the stored failure still references A
    }

    [Fact]
    public async Task ABusyRunStaysVisibleWhateverRouteIsSelectedAndNamesItsOwnPhase()
    {
        var (session, request, runner) = Arrange();
        var run = session.PlayAsync(request);
        Assert.True(runner.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotEmpty(session.HeadlineFor("some-other-route"));
        Assert.False(session.CanReplayFor("some-other-route"));
        runner.Release.Set();
        await run;
    }

    [Fact]
    public void ShutdownDuringLivePlaybackStopsTheRunLeavesNothingAndRaisesNoLateEvents()
    {
        var waiting = new WaitingRunner();
        var (session, request, _) = Arrange(custom: () => waiting);
        var events = 0;
        session.Changed += () => Interlocked.Increment(ref events);
        var run = session.PlayAsync(request);
        Assert.True(waiting.Entered.Wait(TimeSpan.FromSeconds(10)));

        Assert.True(session.ShutdownAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult().Clean);
        var afterShutdown = Volatile.Read(ref events);
        run.Wait(TimeSpan.FromSeconds(10));

        Assert.Equal(afterShutdown, Volatile.Read(ref events));            // nothing was raised once the window was closing
        Assert.True(session.IsClosed);
        Assert.NotEqual(PlaybackState.Finished, session.State);
        Assert.Null(session.Outcome);
        Assert.Empty(Directory.EnumerateDirectories(_store));                // no record, cancelled or not
        Assert.Empty(Directory.EnumerateDirectories(_sbx));                  // the sandbox was deleted
    }

    [Fact]
    public async Task ASessionThatIsShutDownIgnoresFurtherPlays()
    {
        var (session, request, runner) = Arrange();
        Assert.True(session.ShutdownAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult().Clean);
        await session.PlayAsync(request);
        Assert.Equal(0, runner.Runs);
        Assert.Equal(PlaybackState.Idle, session.State);
    }

    [Fact]
    public void ClosingNeverWaitsOnAPendingNotificationAndTheQueuedNotificationIsDroppedWhenItRuns()
    {
        var waiting = new WaitingRunner();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var (_, request, _) = Arrange(custom: () => waiting);
        var service = new ComboPlaybackService(waiting, _store, _sbx);
        var session = new PlaybackSession(service, queue.Enqueue);    // the "UI thread" does not drain the queue until the test says so
        var delivered = 0;
        session.Changed += () => Interlocked.Increment(ref delivered);

        var run = session.PlayAsync(request);
        Assert.True(waiting.Entered.Wait(TimeSpan.FromSeconds(10)));  // the worker reached the engine although nothing drained the queue: posting never blocks it
        Assert.False(queue.IsEmpty);                                  // notifications are pending

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = session.ShutdownAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();   // the "UI thread" waits only for the worker, which never waits for it
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.True(result.Clean, result.Problem);
        run.Wait(TimeSpan.FromSeconds(10));

        while (queue.TryDequeue(out var late)) late();                // the pending notifications finally run — after the close
        Assert.Equal(0, delivered);                                   // none reaches the (closed) listener
        Assert.Equal(0, session.DeliveredNotifications);
        Assert.True(session.DroppedNotifications > 0, "The queued notifications should have been dropped, not delivered.");
        Assert.Empty(Directory.EnumerateDirectories(_store));
        Assert.Empty(Directory.EnumerateDirectories(_sbx));
    }

    [Fact]
    public async Task AShutdownThatCannotStopTheRunReportsItInsteadOfWaitingForever()
    {
        var (session, request, runner) = Arrange();               // GateRunner ignores cancellation until released
        var run = session.PlayAsync(request);
        Assert.True(runner.Entered.Wait(TimeSpan.FromSeconds(10)));

        var result = await session.ShutdownAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(result.Clean);
        Assert.Contains("did not stop", result.Problem);
        runner.Release.Set();
        await run;
    }

    [Fact]
    public async Task ACancelThatArrivesAfterTheCommitIsRefusedByTheSession()
    {
        var (session, request, runner) = Arrange();
        runner.Release.Set();
        await session.PlayAsync(request);
        Assert.False(session.Cancel());                           // nothing is running: not accepted, state stays Finished
        Assert.Equal(PlaybackState.Finished, session.State);
    }

    [Fact]
    public async Task ShutdownReportsASandboxThatPreparationFailureCouldNotRemove()
    {
        var (_, request, _) = Arrange();
        var sbx = Temp2();
        var service = new ComboPlaybackService(new GateRunner(), Temp2(), sbx) { SandboxDeleter = _ => (false, "deliberately locked") };
        var session = new PlaybackSession(service);
        var changes = 0;
        // Changed #1 = play requested, #2 = planning, #3 = preparing: cancel at #3, before the sandbox is built.
        session.Changed += () => { if (Interlocked.Increment(ref changes) == 3) session.Cancel(); };

        await session.PlayAsync(request);
        Assert.Equal(PlaybackState.Cancelled, session.State);
        var result = await session.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Clean);                                  // never a false "clean"
        var left = Assert.Single(result.LeftoverSandboxes);
        Assert.Contains("deliberately locked", left);
        foreach (var d in Directory.EnumerateDirectories(sbx)) RuntimeSandbox.Delete(d);
    }

    private string Temp2() { var d = Path.Combine(Path.GetTempPath(), "xray-ps2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); _cleanup.Add(d); return d; }

    [Fact]
    public void ACallbackThatPassedTheClosedCheckCannotRunItsListenerAfterTheCloseTakesEffect()
    {
        // Deterministic interleaving: the listener is mid-delivery (past the closed check) when another thread closes the session.
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var (_, request, _) = Arrange();
        var session = new PlaybackSession(new ComboPlaybackService(new GateRunner(), Temp2(), Temp2()), queue.Enqueue);
        using var inListener = new ManualResetEventSlim();
        using var letListenerFinish = new ManualResetEventSlim();
        var finishedBeforeClose = false;
        session.Changed += () => { inListener.Set(); letListenerFinish.Wait(TimeSpan.FromSeconds(10)); finishedBeforeClose = !session.IsClosed; };

        _ = session.PlayAsync(request);                       // queues "play requested" (the runner is never reached: we never drain the rest)
        Assert.True(SpinWait.SpinUntil(() => !queue.IsEmpty, 5000));
        queue.TryDequeue(out var first);
        var ui = Task.Run(first!);                            // "UI thread" delivering: now inside the listener
        Assert.True(inListener.Wait(TimeSpan.FromSeconds(10)));

        var closing = Task.Run(() => session.ShutdownAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        Assert.False(SpinWait.SpinUntil(() => session.IsClosed, 300));   // the close waits for the in-flight delivery ...
        letListenerFinish.Set();
        ui.Wait(TimeSpan.FromSeconds(10));
        closing.Wait(TimeSpan.FromSeconds(10));
        Assert.True(finishedBeforeClose);                                 // ... which therefore finished against an open session
        Assert.True(session.IsClosed);

        var deliveredAtClose = session.DeliveredNotifications;
        while (queue.TryDequeue(out var late)) late();                    // everything queued afterwards is dropped
        Assert.Equal(deliveredAtClose, session.DeliveredNotifications);
        Assert.True(session.DroppedNotifications > 0);
    }

    [Fact]
    public void UnderContentionNoListenerRunsOnceClosedHasBeenObserved()
    {
        var (_, baseRequest, _) = Arrange();
        var notReady = baseRequest with { Setup = PlaybackPreflight.Check(baseRequest.Root, "ComboGuy", new AppSettings()) };   // each PlayAsync raises notifications and fails fast
        var exercised = 0;
        for (var round = 0; round < 300; round++)
        {
            var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            var session = new PlaybackSession(new ComboPlaybackService(new GateRunner(), Temp2(), Temp2()), queue.Enqueue);
            var lateDelivery = false;
            session.Changed += () => { if (session.IsClosed) lateDelivery = true; };
            var raiser = Task.Run(async () => { for (var i = 0; i < 20; i++) await session.PlayAsync(notReady); });
            var drain = Task.Run(() => { for (var i = 0; i < 2000; i++) if (queue.TryDequeue(out var a)) a(); });
            var close = Task.Run(() => session.ShutdownAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
            Task.WaitAll(raiser, drain, close);
            var before = session.DeliveredNotifications;
            while (queue.TryDequeue(out var a)) a();
            Assert.False(lateDelivery, "A listener ran after the session was observed closed.");
            exercised += session.DeliveredNotifications + session.DroppedNotifications;
            Assert.Equal(before, session.DeliveredNotifications);
        }
        Assert.True(exercised > 0, "The contention test never produced a notification, so it proved nothing.");
    }

    // ------------------------------------------------------------------ attempt identity, preflight vs runtime, reentrancy, route selection

    private PlaybackRequest Unready(PlaybackRequest r, string engine = "") =>
        r with { Setup = PlaybackPreflight.Check(r.Root, "ComboGuy", new AppSettings { XRayEnginePath = engine.Length == 0 ? null : engine }) };

    [Fact]
    public async Task AnOldResultCanNeverSatisfyAWaitForTheNewAttemptAfterAPreflightRefusal()
    {
        // Codex's sequence: successful A -> configure an unpatched engine -> Play -> preflight refusal -> wait for a verdict.
        var (session, request, runner) = Arrange();
        runner.Release.Set();
        await session.PlayAsync(request);
        Assert.Equal(1, session.AttemptId);
        Assert.Equal(AttemptState.VerdictProduced, session.Attempt);
        Assert.True(session.ResultIsCurrent);
        Assert.Equal(VerdictWaitKind.Verdict, session.VerdictForLatestAttempt().Kind);
        var oldRun = session.ResultRunId;

        var unpatched = Path.Combine(Temp2(), "stock.exe");
        File.WriteAllText(unpatched, "an engine without the hook");
        var refused = PlaybackPreflight.Check(request.Root, "ComboGuy", new AppSettings { XRayEnginePath = unpatched });
        Assert.False(refused.Ready);                                              // preflight refuses (no X-Ray hook) ...
        session.RefuseAttempt(request.Route.Key, string.Join(" ", refused.Issues)); // ... exactly as the panel records it

        Assert.Equal(2, session.AttemptId);                                       // a distinct attempt, although no engine ran
        Assert.Equal(AttemptState.PreflightRefused, session.Attempt);
        Assert.Contains("virtual-input hook", session.AttemptIssue);
        Assert.False(session.ResultIsCurrent);                                    // the old result is on file but is not this attempt's
        Assert.Equal(1, session.ResultAttemptId);
        Assert.Equal(oldRun, session.ResultRunId);
        var wait = session.VerdictForLatestAttempt();
        Assert.Equal(VerdictWaitKind.RefusedPreflight, wait.Kind);                // the wait must NOT be satisfied by the old verdict
        Assert.Contains("no runtime verdict", wait.Message);
        Assert.Contains("refused during preflight", wait.Message);
        Assert.Equal(1, runner.Runs);                                             // no second engine launch happened
    }

    [Fact]
    public async Task PreflightRefusalAndRuntimeInconclusiveAreDifferentOutcomes()
    {
        // Preflight refusal: the setup is rejected before launch; there is no verdict of any kind.
        var (refusedSession, request, _) = Arrange("noinject");
        await refusedSession.PlayAsync(Unready(request));
        Assert.Equal(AttemptState.PreflightRefused, refusedSession.Attempt);
        Assert.Null(refusedSession.Outcome);
        Assert.Equal(VerdictWaitKind.RefusedPreflight, refusedSession.VerdictForLatestAttempt().Kind);

        // Runtime Inconclusive: the engine launched and the verifier said InputInjectionUnavailable.
        var (ranSession, ranRequest, runner) = Arrange("noinject");
        runner.Release.Set();
        await ranSession.PlayAsync(ranRequest);
        Assert.Equal(AttemptState.VerdictProduced, ranSession.Attempt);
        Assert.Equal(VerifyStatus.Inconclusive, ranSession.Outcome!.Report.Status);
        Assert.Equal(VerifyReason.InputInjectionUnavailable, ranSession.Outcome.Report.Reason);
        Assert.Equal(1, runner.Runs);                                             // the engine really ran
        Assert.Equal(VerdictWaitKind.Verdict, ranSession.VerdictForLatestAttempt().Kind);
    }

    [Fact]
    public async Task EveryPressGetsItsOwnAttemptIdAndAttemptStatesAreExplicit()
    {
        var (session, request, runner) = Arrange();
        Assert.Equal((0, AttemptState.None), (session.AttemptId, session.Attempt));
        Assert.Equal(VerdictWaitKind.NoAttempt, session.VerdictForLatestAttempt().Kind);

        var run = session.PlayAsync(request);
        Assert.True(runner.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal((1, AttemptState.RuntimeStarted), (session.AttemptId, session.Attempt));
        Assert.Equal(VerdictWaitKind.Pending, session.VerdictForLatestAttempt().Kind);
        Assert.Equal(request.Route.Key, session.AttemptRouteKey);
        runner.Release.Set();
        await run;

        session.RefuseAttempt("route-x", "setup incomplete");
        Assert.Equal((2, AttemptState.PreflightRefused, "route-x"), (session.AttemptId, session.Attempt, session.AttemptRouteKey));
        await session.ReplayAsync();                                              // replays the last RUN: a third, real attempt
        Assert.Equal((3, AttemptState.VerdictProduced), (session.AttemptId, session.Attempt));
        Assert.True(session.ResultIsCurrent);
        Assert.Equal(3, session.ResultAttemptId);
    }

    [Fact]
    public async Task ACancelledAttemptEndsWithoutAVerdictAndDoesNotInheritAnEarlierOne()
    {
        var (session, request, runner) = Arrange();
        runner.Release.Set();
        await session.PlayAsync(request);
        var waiting = new WaitingRunner();
        var cancellable = new PlaybackSession(new ComboPlaybackService(waiting, Temp2(), Temp2()));
        var run = cancellable.PlayAsync(request);
        Assert.True(waiting.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(cancellable.Cancel());
        await run;
        Assert.Equal(AttemptState.Cancelled, cancellable.Attempt);
        Assert.Equal(VerdictWaitKind.EndedWithoutVerdict, cancellable.VerdictForLatestAttempt().Kind);
        Assert.False(cancellable.ResultIsCurrent);
    }

    [Fact]
    public void ASubscriberThatClosesTheSessionStopsTheLaterSubscribersOfTheSameNotification()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var (_, request, _) = Arrange();
        var session = new PlaybackSession(new ComboPlaybackService(new GateRunner(), Temp2(), Temp2()), queue.Enqueue);
        var ran = new List<string>();
        session.Changed += () => { ran.Add("A"); session.ShutdownAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); };   // A closes the session
        session.Changed += () => ran.Add("B");

        session.RefuseAttempt("r", "x");                       // raises one notification
        Assert.True(queue.TryDequeue(out var delivery));
        delivery!();

        Assert.Equal(["A"], ran);                              // B never starts: the contract is "no subscriber begins after the session is closed"
        Assert.True(session.IsClosed);
        while (queue.TryDequeue(out var late)) late();
        Assert.Equal(["A"], ran);
    }

    [Fact]
    public void WithoutAClosingSubscriberEverySubscriberRunsInOrder()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var (_, request, _) = Arrange();
        var session = new PlaybackSession(new ComboPlaybackService(new GateRunner(), Temp2(), Temp2()), queue.Enqueue);
        var ran = new List<string>();
        session.Changed += () => ran.Add("A");
        session.Changed += () => ran.Add("B");
        session.RefuseAttempt("r", "x");
        queue.TryDequeue(out var delivery);
        delivery!();
        Assert.Equal(["A", "B"], ran);
    }

    [Fact]
    public void RouteSelectionIsExplicitAndNeverPicksTheFirstSubstringMatch()
    {
        var keys = new[] { "cand:neutral>state:200@c0", "cand:neutral>state:2000@c1", "cand:neutral>state:210@c2" };
        Assert.Equal(0, RouteSelection.ByIndex(3, 1));
        Assert.Equal(2, RouteSelection.ByIndex(3, 3));
        Assert.Throws<InvalidOperationException>(() => RouteSelection.ByIndex(3, 0));
        Assert.Throws<InvalidOperationException>(() => RouteSelection.ByIndex(3, 4));
        Assert.Throws<InvalidOperationException>(() => RouteSelection.ByIndex(0, 1));

        Assert.Equal(1, RouteSelection.ByKey(keys, "cand:neutral>state:2000@c1"));
        Assert.Throws<InvalidOperationException>(() => RouteSelection.ByKey(keys, "cand:neutral>state:200"));       // exact means exact: a prefix is not a key
        Assert.Throws<InvalidOperationException>(() => RouteSelection.ByKey(keys, "CAND:NEUTRAL>STATE:200@C0"));     // ordinal, case-sensitive

        Assert.Equal(2, RouteSelection.BySubstring(keys, "state:210"));
        var ambiguous = Assert.Throws<InvalidOperationException>(() => RouteSelection.BySubstring(keys, "state:200"));  // matches #1 and #2: not silently #1
        Assert.Contains("#1", ambiguous.Message);
        Assert.Contains("#2", ambiguous.Message);
        Assert.Throws<InvalidOperationException>(() => RouteSelection.BySubstring(keys, "nothing"));
        Assert.Throws<InvalidOperationException>(() => RouteSelection.ByKey([keys[0], keys[0]], keys[0]));            // duplicate keys are not selectable by key
    }

    // ------------------------------------------------------------------ approach distance and diagnostic scoping

    [Fact]
    public async Task TheConfiguredApproachDistanceReachesThePlanTheFingerprintTheRecordAndTheDiagnostic()
    {
        var (_, request, _) = Arrange();
        var sbx = Temp2();
        var store = Temp2();
        var plan60 = InputPlanner.Plan(request.Graph, request.Route, new PlanOptions { ApproachDistance = 60 }).Plan!;
        var plan45 = InputPlanner.Plan(request.Graph, request.Route, new PlanOptions { ApproachDistance = 45 }).Plan!;
        Assert.NotEqual(InputPlanner.Fingerprint(plan60), InputPlanner.Fingerprint(plan45));   // the threshold is part of the plan's identity

        var runner = new GateRunner();
        runner.Release.Set();
        var session = new PlaybackSession(new ComboPlaybackService(runner, store, sbx));
        await session.PlayAsync(request with { Setup = request.Setup with { ApproachDistance = 45 } });

        var o = session.Outcome!;
        Assert.Equal(45, o.Plan!.ApproachDistance);                                   // the plan that actually ran
        Assert.Contains("\"approachDistance\": 45", File.ReadAllText(o.Record.PlanPath));
        Assert.Equal(45, o.Record.ApproachDistance);
        Assert.Equal(InputPlanner.Fingerprint(plan45), o.Report.PlanFingerprint);
        Assert.Equal(45, new ComboPlaybackService(runner, store, sbx).List().Single().ApproachDistance);   // remembered in the saved record
        var d = session.DiagnosticForLatestAttempt()!;
        Assert.Equal(45, d.Setup.ApproachDistance);
        Assert.Equal(45, d.Runtime is null ? 45 : d.Runtime.Spacing.ConfiguredApproachDistance);
    }

    [Fact]
    public async Task ADiagnosticBelongsToExactlyTheAttemptThatProducedIt()
    {
        var (session, request, runner) = Arrange();
        runner.Release.Set();
        await session.PlayAsync(request);                                             // attempt 1: a real run
        var first = session.DiagnosticForLatestAttempt()!;
        Assert.Equal(("run", 1, session.ResultRunId), (first.Kind, first.Attempt.Id, first.Result!.RunId));
        Assert.Equal(first.ToJson(), session.DiagnosticForLatestAttempt()!.ToJson());  // a snapshot: asking again changes nothing
        Assert.NotNull(first.Character);
        Assert.NotEmpty(first.Character!.Files);
        Assert.Equal(first.ToJson(), File.ReadAllText(session.Outcome!.Record.DiagnosticPath).Replace("\r\n", "\n"));   // persisted beside the record

        // A press refused before launch is its own attempt: it must not inherit attempt 1's run, verdict, engine hash or telemetry.
        var unpatched = Path.Combine(Temp2(), "stock.exe");
        File.WriteAllText(unpatched, "no hook");
        var refused = PlaybackPreflight.Check(request.Root, "ComboGuy", new AppSettings { XRayEnginePath = unpatched });
        session.RefuseAttempt(request.Route.Key, string.Join(" ", refused.Issues), refused);
        var second = session.DiagnosticForLatestAttempt()!;
        Assert.Equal(("refused", 2, null), (second.Kind, second.Attempt.Id, second.Result?.RunId));
        Assert.Null(second.Runtime);
        Assert.Null(second.Engine);
        Assert.DoesNotContain(first.Result.RunId!, second.ToJson());
        Assert.Contains("no runtime verdict belongs to this attempt", second.ToText());
        Assert.Contains("virtual-input hook", second.Attempt.Issue);

        // Another selected route never exposes this attempt's diagnostic.
        Assert.Null(session.DiagnosticFor("some-other-route"));
        Assert.Null(session.DiagnosticFor(null));
        Assert.NotNull(session.DiagnosticFor(request.Route.Key));

        // Replay is a new attempt with a new run; the first run's evidence is not reused.
        await session.ReplayAsync();
        var third = session.DiagnosticForLatestAttempt()!;
        Assert.Equal(("run", 3), (third.Kind, third.Attempt.Id));
        Assert.NotEqual(first.Result.RunId, third.Result!.RunId);
        Assert.Equal(3, third.Result.AttemptId);
    }

    [Fact]
    public async Task ACancelledAttemptHasADiagnosticWithNoRuntimeEvidence()
    {
        var (_, request, _) = Arrange();
        var waiting = new WaitingRunner();
        var session = new PlaybackSession(new ComboPlaybackService(waiting, Temp2(), Temp2()));
        var run = session.PlayAsync(request);
        Assert.True(waiting.Entered.Wait(TimeSpan.FromSeconds(10)));
        session.Cancel();
        await run;
        var d = session.DiagnosticForLatestAttempt()!;
        Assert.Equal(("ended", "Cancelled"), (d.Kind, d.Attempt.State));
        Assert.Null(d.Result);
        Assert.Null(d.Runtime);
        Assert.NotNull(d.Character);                                                  // the static snapshot taken at attempt start is still there
    }

    [Fact]
    public async Task AFailedRunDiagnosticCarriesTheExpectedStepFromTheAttemptStartSnapshot()
    {
        var (session, request, runner) = Arrange("dropped");
        runner.Release.Set();
        await session.PlayAsync(request);
        var d = session.DiagnosticForLatestAttempt()!;
        Assert.Equal("Failed", d.Result!.Verdict);
        var failed = Assert.Single(d.StaticSteps, x => x.Index == d.FailedStep);
        Assert.Equal(d.Route.Single(r => r.Index == d.FailedStep).Edge, failed.EdgeId);
        Assert.Contains("executed controller: unknown", d.ToText());
    }

    // ------------------------------------------------------------------ full static context, immutable save

    [Fact]
    public async Task AVerifiedRunDiagnosticCarriesTheStaticContextOfEveryRouteStep()
    {
        var (session, request, runner) = Arrange();
        runner.Release.Set();
        await session.PlayAsync(request);
        Assert.Equal(VerifyStatus.Verified, session.Outcome!.Report.Status);
        var d = session.DiagnosticForLatestAttempt()!;
        Assert.Null(d.FailedStep);
        Assert.Equal(request.Route.Steps.Count, d.StaticSteps.Count);             // every step, not only a failed one
        Assert.Equal(d.Route.Select(r => r.Edge), d.StaticSteps.Select(x => x.EdgeId));
        foreach (var step in d.StaticSteps)
        {
            Assert.False(string.IsNullOrEmpty(step.ControllerId));
            Assert.NotEmpty(step.ModelledRequirements);
            Assert.NotNull(step.ControllerSource);
            Assert.NotEmpty(step.TriggerGroups.SelectMany(g => g.Lines));         // numbered trigger groups with their source lines
            Assert.NotEmpty(step.Commands);
        }

        var text = d.ToText();
        for (var i = 1; i <= d.StaticSteps.Count; i++) Assert.Contains($"static step {i}", text);
        Assert.Contains("unmodelled expressions (as written)", text);
    }

    [Fact]
    public async Task ADiagnosticSavedWhileAnotherAttemptIsRefusedStillBelongsToItsOwnRun()
    {
        var store = Temp2();
        var runner = new GateRunner();
        runner.Release.Set();
        var (_, request, _) = Arrange();
        PlaybackSession? session = null;
        var fired = 0;
        session = new PlaybackSession(new ComboPlaybackService(runner, store, Temp2()))
        {
            // The race window: A's result is published, A's diagnostic is not yet written — and attempt B is refused right now.
            SaveSeam = p => { if (p == "before-diagnostic-save" && Interlocked.Increment(ref fired) == 1) session!.RefuseAttempt("another-route", "refused during A's save"); }
        };
        await session.PlayAsync(request);

        Assert.Equal((2, AttemptState.PreflightRefused), (session.AttemptId, session.Attempt));   // the session has moved on to attempt 2
        var folder = Assert.Single(Directory.EnumerateDirectories(store));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "diagnostic.json")));
        var root = doc.RootElement;
        Assert.Equal("run", root.GetProperty("kind").GetString());
        Assert.Equal(1, root.GetProperty("attempt").GetProperty("id").GetInt32());                // A's identity, not B's
        Assert.Equal("VerdictProduced", root.GetProperty("attempt").GetProperty("state").GetString());
        Assert.Equal(Path.GetFileName(folder), root.GetProperty("result").GetProperty("runId").GetString());   // A's run in A's folder
        Assert.Equal(request.Route.Key, root.GetProperty("attempt").GetProperty("routeKey").GetString());
        Assert.DoesNotContain("refused during A's save", File.ReadAllText(Path.Combine(folder, "diagnostic.json")));
    }

    [Fact]
    public async Task ADiagnosticSavedWhileAReplayStartsNeverMixesTheTwoAttempts()
    {
        var store = Temp2();
        var runner = new GateRunner();
        runner.Release.Set();
        var (_, request, _) = Arrange();
        PlaybackSession? session = null;
        var fired = 0;
        session = new PlaybackSession(new ComboPlaybackService(runner, store, Temp2()))
        {
            SaveSeam = p => { if (p == "before-diagnostic-save" && Interlocked.Increment(ref fired) == 1) _ = session!.ReplayAsync(); }   // B begins while A's save is pending
        };
        await session.PlayAsync(request);
        Assert.True(SpinWait.SpinUntil(() => session.AttemptId == 2 && session.Attempt == AttemptState.VerdictProduced, 15000), "the replay never finished");

        var folders = Directory.EnumerateDirectories(store).ToList();
        Assert.Equal(2, folders.Count);
        var attempts = new List<int>();
        foreach (var folder in folders)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "diagnostic.json")));
            Assert.Equal(Path.GetFileName(folder), doc.RootElement.GetProperty("result").GetProperty("runId").GetString());   // each folder holds its own run's diagnostic
            Assert.Equal(doc.RootElement.GetProperty("attempt").GetProperty("id").GetInt32(), doc.RootElement.GetProperty("result").GetProperty("attemptId").GetInt32());
            attempts.Add(doc.RootElement.GetProperty("attempt").GetProperty("id").GetInt32());
        }

        Assert.Equal([1, 2], attempts.OrderBy(x => x).ToList());
    }
}
