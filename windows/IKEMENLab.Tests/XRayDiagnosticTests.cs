using System.Text.Json;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Permanent regression fixtures from two real user-found playbacks.
/// Funny Valentine (m4_fv_*): the REAL plan and trace from a Windows run. Goku (m4_goku_reconstructed_*): RECONSTRUCTED from the frame numbers the user reported
/// (see scripts/make_m4_goku_reconstructed_fixture.py), kept only because the verifier tests need a plan and the real Goku plan is unavailable. The REAL Goku trace is
/// m4_goku_real_trace.jsonl (see XRayDiagnosticAnchoringTests).
/// </summary>
public class XRayDiagnosticTests : IDisposable
{
    private readonly List<string> _cleanup = [];
    public void Dispose() { foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ } }

    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static (InputPlan Plan, TraceLog Log) Fv(Func<string, string>? editTrace = null)
    {
        var plan = InputPlanner.FromJson(File.ReadAllText(Fx("m4_fv_plan.json")));
        var text = File.ReadAllText(Fx("m4_fv_trace.jsonl"));
        return (plan, TraceReader.Read(editTrace is null ? text : editTrace(text)));
    }

    private static (InputPlan Plan, TraceLog Log) GokuReconstructed(Func<string, string>? editTrace = null)
    {
        var plan = InputPlanner.FromJson(File.ReadAllText(Fx("m4_goku_reconstructed_plan.json")));
        var text = File.ReadAllText(Fx("m4_goku_reconstructed_trace.jsonl")).Replace("\"planFingerprint\":null", "\"planFingerprint\":\"" + InputPlanner.Fingerprint(plan) + "\"");
        return (plan, TraceReader.Read(editTrace is null ? text : editTrace(text)));
    }

    // ------------------------------------------------------------------ the fixtures themselves

    [Fact]
    public void TheRealFunnyValentineTraceBelongsToItsPlan()
    {
        var (plan, log) = Fv();
        Assert.Equal(log.Meta!.PlanFingerprint, InputPlanner.Fingerprint(plan));   // same hash on Windows (CRLF) and here: the fingerprint is newline-normalised
        Assert.Equal(60, plan.ApproachDistance);
        Assert.Empty(log.Issues);
    }

    [Fact]
    public void PlanJsonRoundTripsThroughTheReader()
    {
        var (plan, _) = Fv();
        var again = InputPlanner.FromJson(InputPlanner.ToJson(plan));
        Assert.Equal(InputPlanner.Fingerprint(plan), InputPlanner.Fingerprint(again));
        Assert.Throws<FormatException>(() => InputPlanner.FromJson("{\"schema\":\"nope\"}"));
        Assert.Throws<FormatException>(() => InputPlanner.FromJson("not json"));
    }

    // ------------------------------------------------------------------ Goku: the first decisive wrong move

    [Fact]
    public void ReconstructedGokuKeepsWrongStateAndRecordsTheFirstMismatchAtFrame291()
    {
        var (plan, log) = GokuReconstructed();
        var report = RouteVerifier.Verify(plan, log);
        Assert.Equal((VerifyStatus.Failed, VerifyReason.WrongState, 1), (report.Status, report.Reason, report.FailedStep));

        var ev = report.Steps[0].Evidence!;
        Assert.Equal(17200, ev.ExpectedState);
        Assert.Equal(200, ev.FirstMismatchState);
        Assert.Equal(291, ev.FirstMismatchFrame);
        Assert.False(ev.ExpectedStateEverObserved);
        Assert.True(ev.InputAttempted);
        Assert.Equal(["a"], ev.AttemptedKeys);
        Assert.Equal(290, ev.InputAttemptFrame);
        Assert.Equal(PrerequisiteFailure.DifferentMoveEntered, ev.Failure);
        Assert.Equal("unknown", ev.ExecutedController);                         // the destination state does not identify the controller

        // The later return to State 0 (frame 308) and the driver's timeout (frame 338) are claims/aftermath, not the decisive mismatch.
        Assert.Equal("expected state 17200, saw 0", ev.DriverClaim);
        Assert.Equal(338, ev.DriverClaimFrame);
        Assert.Equal("Step 1 failed: different move. Expected State 17200. Observed State 200 at frame 291 after the reported a input. State 17200 was never observed.",
            report.Steps[0].Detail);
    }

    [Fact]
    public void ReconstructedGokuInspectionFocusesFrame291AndDoesNotClaimAControllerWon()
    {
        var (plan, log) = GokuReconstructed();
        var report = RouteVerifier.Verify(plan, log);
        var f = PlaybackInspector.Inspect(report, log)!;
        Assert.Equal(291, f.FocusFrame);
        Assert.Contains(f.Window, r => r.Frame == 291);
        Assert.Equal("Step 1 failed: different move.", f.Headline);
        var hints = string.Join(" ", f.Hints);
        Assert.DoesNotContain("took priority", hints);
        Assert.DoesNotContain("won", hints);
        Assert.Contains("not established", hints);                                // a static explanation is offered only as a possibility
        Assert.Contains("which state was entered, not which controller ran", hints);
    }

    [Fact]
    public void AMismatchFollowedByTheExpectedStateIsSaidToHaveBeenObservedLater()
    {
        // Same wrong move at 291, but the expected state appears at 330: the first mismatch is still frame 291, and the text no longer says "never".
        var (plan, log) = GokuReconstructed(t => t.Replace("\"frame\":330,", "\"frame\":330,").Replace(
            "\"frame\":330,\"engineTick\":332,\"round\":1,\"p1\":{\"state\":0,\"prevState\":null,\"ctrl\":true,\"stateType\":\"S\",\"moveType\":\"I\",\"anim\":0",
            "\"frame\":330,\"engineTick\":332,\"round\":1,\"p1\":{\"state\":17200,\"prevState\":null,\"ctrl\":false,\"stateType\":\"S\",\"moveType\":\"A\",\"anim\":0"));
        var step = PlanStepOf(plan);
        var ev = StepEvidenceBuilder.Build(step, log.Frames.Where(f => f.Frame >= 290).ToList(), 0, log.Events.OfType<InputEvent>().Where(e => e.Step == 1 && e.Keys.Count > 0).ToList(),
            log.Events.OfType<DriverEvent>().ToList(), plan.ApproachDistance);
        Assert.Equal((200, 291L), (ev.FirstMismatchState, ev.FirstMismatchFrame));
        Assert.True(ev.ExpectedStateEverObserved);
        Assert.Equal(330, ev.ExpectedStateFirstFrame);
        Assert.Contains("observed later, at frame 330", FailureNarrative.Describe(step, ev, VerifyReason.WrongState));
    }

    private static PlanStep PlanStepOf(InputPlan plan) => plan.Steps[0];

    // ------------------------------------------------------------------ Funny Valentine: source state seen, contact missing

    [Fact]
    public void ValentineKeepsItsVerdictAndNamesTheMissingContact()
    {
        var (plan, log) = Fv();
        var report = RouteVerifier.Verify(plan, log);
        Assert.Equal((VerifyStatus.Failed, VerifyReason.PreconditionNeverMet, 2), (report.Status, report.Reason, report.FailedStep));   // verdict unchanged

        var ev = report.Steps[1].Evidence!;
        Assert.True(ev.SourceStateObserved);
        Assert.Equal((274L, 287L), (ev.SourceOccurrenceStartFrame, ev.SourceOccurrenceEndFrame));
        Assert.Equal("contact", ev.RequiredContact);
        Assert.False(ev.RequiredContactObserved);
        Assert.False(ev.InputAttempted);
        Assert.Null(ev.TimingSatisfied);                                         // no timing requirement: not applicable, not "false"
        Assert.False(ev.ExpectedStateEverObserved);
        Assert.Equal(PrerequisiteFailure.RequiredContactNotObserved, ev.Failure);
        Assert.Equal("precondition never met for cand:state:200>state:210@state:200/ctrl:6#0", ev.DriverClaim);   // kept as the driver's claim only
        Assert.StartsWith("Step 2 was not attempted: the opening attack did not connect. State 200 occurred", report.Steps[1].Detail);
        Assert.DoesNotContain("never happened", report.Steps[1].Detail);         // the old, wrong explanation
    }

    [Fact]
    public void ValentineSpacingKeepsTheConfiguredThresholdAndTheDerivedMeasurementApart()
    {
        var (plan, log) = Fv();
        var ev = RouteVerifier.Verify(plan, log).Steps[1].Evidence!;
        Assert.Equal(60, ev.ConfiguredApproachDistance);
        Assert.Equal((59.0, 60.0), (ev.SeparationMin, ev.SeparationMax));       // ≈ 60–59 while State 200 ran
        Assert.Equal(["derived:p2.x-p1.x"], ev.SeparationSources);
        var f = PlaybackInspector.Inspect(RouteVerifier.Verify(plan, log), log)!;
        Assert.Equal("Step 2 was not attempted: the opening attack did not connect.", f.Headline);
        Assert.Equal(287, f.FocusFrame);
        Assert.Contains("threshold and a measurement, not the attack's range", string.Join(" ", f.Hints));
        Assert.Contains("Approach distance", string.Join(" ", f.Hints));
        Assert.Contains("Nothing is retried automatically", string.Join(" ", f.Hints));
    }

    [Fact]
    public void MissingContactTelemetryIsReportedAsUnknownNotAsNoContact()
    {
        var (plan, log) = Fv(t => t.Replace("\"moveHit\":0,\"moveContact\":0", "\"moveHit\":null,\"moveContact\":null"));
        var report = RouteVerifier.Verify(plan, log);
        var ev = report.Steps[1].Evidence!;
        Assert.Null(ev.RequiredContactObserved);                                  // cannot be measured: not "false"
        Assert.NotEqual(PrerequisiteFailure.RequiredContactNotObserved, ev.Failure);
        var diag = PlaybackDiagnostic.Build(DiagSource(report, log, plan));
        Assert.Contains("p1.moveContact", diag.Runtime!.MissingTelemetry);
        Assert.Contains("p1.moveHit", diag.Runtime.MissingTelemetry);
    }

    [Fact]
    public void ExplicitPrerequisitesAreDistinguishedByTheirEvidence()
    {
        var (plan, log) = Fv();
        var frames = log.Frames.Where(f => f.Frame >= 273).ToList();
        var inputs = log.Events.OfType<InputEvent>().ToList();
        var drivers = log.Events.OfType<DriverEvent>().ToList();
        var cursor = frames.FindIndex(f => f.P1.State == 200);
        var noTiming = StepEvidenceBuilder.Build(plan.Steps[1] with { EarliestTick = 30, Contact = null }, frames, cursor, [], drivers, 60);
        Assert.Null(noTiming.TimingSatisfied);                                   // no input was attempted: input timing is not inferred from how long the state lasted
        Assert.Null(noTiming.SourceTickAtInput);

        var neverThere = StepEvidenceBuilder.Build(plan.Steps[1] with { FromState = 999 }, frames, cursor, [], drivers, 60);
        Assert.False(neverThere.SourceStateObserved);
        Assert.Equal(PrerequisiteFailure.SourceStateNeverObserved, neverThere.Failure);

        var noInput = StepEvidenceBuilder.Build(plan.Steps[1] with { Contact = null }, frames, cursor, [], drivers, 60);
        Assert.Equal(PrerequisiteFailure.InputNotAttempted, noInput.Failure);
        Assert.Null(FailureNarrative.Describe(plan.Steps[1], noInput, VerifyReason.PreconditionNeverMet));
        _ = inputs;
    }

    // ------------------------------------------------------------------ the diagnostic

    private static PlaybackDiagnostic.Source DiagSource(VerificationReport report, TraceLog log, InputPlan plan, StaticSnapshot? snapshot = null, int attempt = 1)
    {
        var record = new PlaybackRecord("run-1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), report.Character, report.RouteKey, "fixture", report.Status.ToString(), report.Reason, report.FailedStep,
            "kfm", "stages/kfm.def", report.EngineSha256, 1, Path.GetTempPath()) { ApproachDistance = plan.ApproachDistance };
        var outcome = new PlaybackOutcome(record, report, log) { Plan = plan, Snapshot = snapshot };
        return new PlaybackDiagnostic.Source(attempt, AttemptState.VerdictProduced, null, report.RouteKey, null, null, snapshot, outcome, attempt);
    }

    [Fact]
    public void TheDiagnosticOfTheValentineRunIsDeterministicAndHonestAboutWhatItDoesNotKnow()
    {
        var (plan, log) = Fv();
        var report = RouteVerifier.Verify(plan, log);
        var a = PlaybackDiagnostic.Build(DiagSource(report, log, plan));
        var b = PlaybackDiagnostic.Build(DiagSource(report, log, plan));
        Assert.Equal(a.ToJson(), b.ToJson());
        Assert.Equal(a.ToText(), b.ToText());
        Assert.DoesNotContain("\r", a.ToJson());
        Assert.DoesNotContain("\r", a.ToText());

        using var doc = JsonDocument.Parse(a.ToJson());
        var root = doc.RootElement;
        Assert.Equal("ikemenlab.xray.diagnostic/1", root.GetProperty("schema").GetString());
        Assert.Equal("run", root.GetProperty("kind").GetString());
        Assert.Equal(2, root.GetProperty("failedStep").GetInt32());
        var runtime = root.GetProperty("runtime");
        Assert.Equal("unknown", runtime.GetProperty("executedController").GetString());
        Assert.Equal(60, runtime.GetProperty("spacing").GetProperty("configuredApproachDistance").GetInt32());
        Assert.Equal(287, runtime.GetProperty("decisiveFrame").GetInt64());
        Assert.True(runtime.GetProperty("decisiveWindow").GetArrayLength() <= 2 * PlaybackDiagnostic.WindowRadius + 1);
        Assert.Equal(report.PlanFingerprint, root.GetProperty("setup").GetProperty("planFingerprint").GetString());
        Assert.Equal(report.EngineSha256, root.GetProperty("engine").GetProperty("sha256").GetString());
        Assert.Equal("kfm", root.GetProperty("setup").GetProperty("dummy").GetString());
        Assert.Contains(a.Runtime!.DriverReportedInputs, i => i.Frame == 273 && i.Keys.SequenceEqual(["a"]));

        var text = a.ToText();
        Assert.Contains("executed controller: unknown", text);
        Assert.Contains("Step 2 was not attempted: the opening attack did not connect.", text);
        Assert.Contains("source state observed: yes", text);
        Assert.Contains("required contact: contact  observed: no", text);
        Assert.Contains("Step 2 input was not attempted.", text);
        Assert.Contains("configured approach distance: 60", text);
        Assert.Contains("telemetry at the attempted input: n/a — Step 2 input was not attempted", text);   // case A: Step 2 genuinely had no input
        Assert.Contains("derived separation at the attempted input: n/a — Step 2 input was not attempted", text);
        Assert.Contains("first mismatch: state n/a at frame n/a", text);   // a never-attempted step has no "wrong move": the move merely ended
        Assert.Contains("derived separation over the source-occurrence: start 60, end 59, min 59, max 60", text);
        Assert.Contains("source occurrence start: frame 274", text);
        Assert.Contains("does not mean the engine has none", text);
        Assert.DoesNotContain("took priority", text);
    }

    [Fact]
    public void TheReconstructedGokuDiagnosticFocusesTheFirstMismatch()
    {
        var (plan, log) = GokuReconstructed();
        var d = PlaybackDiagnostic.Build(DiagSource(RouteVerifier.Verify(plan, log), log, plan));
        Assert.Equal(291, d.Runtime!.DecisiveFrame);
        Assert.Equal((200, 291L), (d.Runtime.Failure!.FirstMismatchState, d.Runtime.Failure.FirstMismatchFrame));
        Assert.Contains("f291*", d.ToText());
        Assert.Contains(d.Runtime.DecisiveWindow, r => r.Frame == 291 && r.P1State == 200);
        Assert.Contains("Step 1 failed: different move. Expected State 17200. Observed State 200 at frame 291", d.ToText());
    }

    // ------------------------------------------------------------------ static snapshot (a real character on disk)

    private (string Root, CharacterEntry Entry) TwoControllersSameCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xray-diag-" + Guid.NewGuid().ToString("N"));
        _cleanup.Add(root);
        var dir = Path.Combine(root, "chars", "Dup");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Dup.def"), "[Info]\nname = \"Dup\"\n[Files]\ncmd = Dup.cmd\ncns = Dup.cns\nstcommon = common1.cns\n");
        File.WriteAllText(Path.Combine(dir, "Dup.cmd"), "[Command]\nname = \"x\"\ncommand = x\n\n[Statedef -1]\n\n[State -1, first]\ntype = ChangeState\nvalue = 200\ntriggerall = command = \"x\"\n" +
                                                           "trigger1 = statetype = S && ctrl\n\n[State -1, second]\ntype = ChangeState\nvalue = 210\ntriggerall = command = \"x\"\ntrigger1 = statetype = S && ctrl && var(3) = 1\n");
        File.WriteAllText(Path.Combine(dir, "Dup.cns"), "[Statedef 200]\ntype = S\nmovetype = A\nctrl = 0\n\n[State 200, hit]\ntype = HitDef\ntrigger1 = 1\nattr = S, NA\ndamage = 10\n\n" +
                                                           "[Statedef 210]\ntype = S\nmovetype = A\nctrl = 0\n\n[State 210, hit]\ntype = HitDef\ntrigger1 = 1\nattr = S, NA\ndamage = 10\n");
        return (root, new CharacterEntry { Id = "Dup", DisplayName = "Dup", Name = "Dup", Author = "", VersionDate = "", DefPath = "chars/Dup/Dup.def", FolderPath = "chars/Dup" });
    }

    private static ComboRoute RouteTo(CandidateGraph g, string state) =>
        ComboSearch.Find(g, new ComboOptions { MaxMoves = 1, Top = 50, HitConfirmOnly = false }).Routes.First(r => r.States.Single() == state);

    [Fact]
    public void TheSnapshotNamesTheExpectedControllerItsTriggersAndTheStaticallyRelatedOnes()
    {
        var (root, entry) = TwoControllersSameCommand();
        var graph = CandidateGraph.Build(CharacterSemanticIndexer.Build(root, entry));
        var snap = StaticSnapshot.Capture(graph, RouteTo(graph, "state:200"));
        var step = Assert.Single(snap.Steps);

        Assert.StartsWith("state:-1/ctrl:", step.ControllerId);
        Assert.Contains("command = \"x\"", step.ModelledRequirements);
        Assert.Contains(step.TriggerAll, t => t.Text.Contains("command") && t.Source is { File: var f } && f.EndsWith("Dup.cmd"));
        Assert.Contains(step.TriggerGroups.SelectMany(g => g.Lines), t => t.Source is { StartLine: > 0 });
        Assert.Contains(step.TriggerGroups, g => g.IsExpectedBranch);
        Assert.Equal("Inferred", step.Confidence);                                 // a start from the heuristic "neutral" node is Inferred at best
        Assert.NotEmpty(snap.Files);
        Assert.All(snap.Files, f => Assert.False(string.IsNullOrEmpty(f.ContentHash)));   // the index's own content hash of each source file

        var other = Assert.Single(step.CompetingControllers);                     // the other controller reading command x from the same source
        // A competitor is exported with the same captured detail as the expected controller, not just an identity.
        Assert.Contains("command = \"x\"", other.ModelledRequirements);
        Assert.Contains(other.TriggerAll, t => t.Text.Contains("command"));
        Assert.Contains(other.TriggerGroups.SelectMany(g => g.Lines), t => t.Text.Contains("var(3) = 1") && t.Source is { StartLine: > 0 });
        Assert.NotEmpty(other.Unmodelled);                                         // var(3) = 1 is a condition the index does not model
        Assert.Contains(other.Unmodelled, u => u.Contains("var"));
        Assert.NotNull(other.ControllerSource);
        Assert.EndsWith("Dup.cmd", other.ControllerSource!.File);
        Assert.Equal("state:210", other.TargetId);
        Assert.Equal(["x"], other.SharedCommands);
        Assert.Equal("later in the same Statedef", other.FileOrder);              // file order only; never "has priority"
        Assert.False(step.CompetingControllersTruncated);
        Assert.Contains(snap.Limitations, l => l.Contains("executedController = unknown"));
        Assert.Contains(snap.Limitations, l => l.Contains("does not mean the engine has none"));
    }

    [Fact]
    public void TheSnapshotIsBoundedAndSerialisesWithoutLiveFileAccess()
    {
        var (root, entry) = TwoControllersSameCommand();
        var graph = CandidateGraph.Build(CharacterSemanticIndexer.Build(root, entry));
        var snap = StaticSnapshot.Capture(graph, RouteTo(graph, "state:200"));
        Assert.True(snap.Steps[0].CompetingControllers.Count <= StaticSnapshot.MaxCompeting);

        // Edit the character on disk and re-index: the captured snapshot (and a diagnostic built from it) does not change.
        var before = JsonSerializer.Serialize(snap);
        File.WriteAllText(Path.Combine(root, "chars", "Dup", "Dup.cmd"), "[Command]\nname = \"x\"\ncommand = x\n\n[Statedef -1]\n\n[State -1, only]\ntype = ChangeState\nvalue = 999\ntriggerall = command = \"x\"\n");
        _ = CandidateGraph.Build(CharacterSemanticIndexer.Build(root, entry));
        Assert.Equal(before, JsonSerializer.Serialize(snap));
    }

    // ------------------------------------------------------------------ approach distance

    [Theory]
    [InlineData(null, 60, true)]
    [InlineData("", 60, true)]
    [InlineData("   ", 60, true)]
    [InlineData("45", 45, true)]
    [InlineData(" 120 ", 120, true)]
    [InlineData("1", 1, true)]
    [InlineData("1000", 1000, true)]
    [InlineData("0", 60, false)]
    [InlineData("1001", 60, false)]
    [InlineData("-5", 60, false)]
    [InlineData("6.5", 60, false)]
    [InlineData("abc", 60, false)]
    public void ApproachDistanceIsValidatedNeverSilentlyReplaced(string? text, int expected, bool ok)
    {
        Assert.Equal(ok, PlaybackPreflight.TryParseApproach(text, out var value, out var problem));
        Assert.Equal(expected, value);
        Assert.Equal(!ok, problem is not null);
    }

    [Fact]
    public void AnInvalidApproachDistanceIsAPreflightIssueAndAValidOneIsCarried()
    {
        var root = Path.Combine(Path.GetTempPath(), "xray-appr-" + Guid.NewGuid().ToString("N"));
        _cleanup.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "chars", "a")); Directory.CreateDirectory(Path.Combine(root, "chars", "kfm")); Directory.CreateDirectory(Path.Combine(root, "stages"));
        File.WriteAllText(Path.Combine(root, "chars", "kfm", "kfm.def"), "[Files]\n"); File.WriteAllText(Path.Combine(root, "stages", "s.def"), "[StageInfo]\n");
        var engine = Path.Combine(root, "x.exe");
        File.WriteAllText(engine, PlaybackPreflight.HookName);

        var bad = PlaybackPreflight.Check(root, "a", new AppSettings { XRayEnginePath = engine, XRayApproachDistance = "abc" });
        Assert.False(bad.Ready);
        Assert.Contains(bad.Issues, i => i.Contains("Approach distance"));

        var good = PlaybackPreflight.Check(root, "a", new AppSettings { XRayEnginePath = engine, XRayApproachDistance = "45" });
        Assert.True(good.Ready, string.Join(" | ", good.Issues));
        Assert.Equal(45, good.ApproachDistance);
        Assert.Equal(60, PlaybackPreflight.Check(root, "a", new AppSettings { XRayEnginePath = engine }).ApproachDistance);   // default unchanged
    }

    [Fact]
    public void TheApproachDistanceIsRememberedLikeTheOtherPlaybackSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "xray-settings-" + Guid.NewGuid().ToString("N") + ".json");
        _cleanup.Add(Path.GetDirectoryName(path)!);   // the temp dir itself is shared; the file is removed below
        try
        {
            var store = new JsonSettingsStore(path);
            store.Save(new AppSettings { IkemenRoot = "R", XRayApproachDistance = "42", XRayDummy = "kfm" });
            var loaded = new JsonSettingsStore(path).Load();
            Assert.Equal(("R", "42", "kfm"), (loaded.IkemenRoot, loaded.XRayApproachDistance, loaded.XRayDummy));
        }
        finally { File.Delete(path); _cleanup.Clear(); }
    }
}
