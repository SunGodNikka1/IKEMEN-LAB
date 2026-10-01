using System.Text.Json;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>Evidence must describe the occurrence and attempt the verifier actually judged, and label every measurement for what it is.</summary>
public class XRayDiagnosticAnchoringTests
{
    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // ------------------------------------------------------------------ a tiny synthetic run

    private const string Edge1 = "cand:neutral>state:200@c0";
    private const string Edge2 = "cand:state:200>state:210@c1";

    private static InputPlan Plan(int? earliestTick) => new("char:Synth", Edge1 + ">" + Edge2,
    [
        new PlanStep(1, Edge1, "Start", "neutral", "state:200", null, 200, "a", [new InputFrame(["a"]), new InputFrame([])], null, null, 45, []),
        new PlanStep(2, Edge2, "Cancel", "state:200", "state:210", 200, 210, "a", [new InputFrame(["a"]), new InputFrame([])], null, earliestTick, 45, [])
    ], 60, 20, 20, 1800, []);

    private static PlayerSample P1(int state, double x = 0) =>
        new(state, null, state == 0, "S", state == 0 ? "I" : "A", null, null, 3000, 0, x, 0, 0, 0, 1, 0, 0, null);

    private static PlayerSample P2() => new(0, null, true, "S", "I", null, null, 3000, 0, 50, 0, 0, 0, -1, 0, 0, null);

    /// <summary>Frames 1..last. <paramref name="p1State"/> gives P1's state per frame. Step 1's input is at frame 20; <paramref name="step2Input"/> is the frame of step 2's input.</summary>
    private static (InputPlan Plan, TraceLog Log) Run(int? earliestTick, Func<long, int> p1State, long step2Input, long last)
    {
        var plan = Plan(earliestTick);
        var events = new List<TraceEvent>
        {
            new TraceMeta("ikemenlab.xray.trace/0", "synthetic", "0.2", "Synth", "other", new Dictionary<string, bool>(), ["hook:loop"], InputPlanner.Fingerprint(plan)),
            new DriverEvent(1, null, "plan_start", null, plan.RouteKey)
        };
        for (long f = 1; f <= last; f++)
        {
            events.Add(new FrameEvent(f, f + 2, 1, P1(p1State(f), -10 + f % 3), P2(), 60 - f % 3, null, null, null, "derived:p2.x-p1.x"));
            if (f == 19) { events.Add(new DriverEvent(f, null, "step_wait", 1, Edge1)); events.Add(new DriverEvent(f, null, "step_ready", 1, Edge1)); }
            if (f == 20) events.Add(new InputEvent(f, null, 1, ["a"], 1, "input"));
            if (f == 21) events.Add(new InputEvent(f, null, 1, [], 1, "input"));
            if (f == 22) { events.Add(new DriverEvent(f, null, "step_done", 1, "200")); }
            if (f == 23) events.Add(new DriverEvent(f, null, "step_wait", 2, Edge2));
            if (f == step2Input - 1) events.Add(new DriverEvent(f, null, "step_ready", 2, Edge2));
            if (f == step2Input) events.Add(new InputEvent(f, null, 1, ["a"], 2, "input"));
            if (f == step2Input + 1) events.Add(new InputEvent(f, null, 1, [], 2, "input"));
        }

        events.Add(new EndEvent(last, null, "stepTimeout"));
        return (plan, new TraceLog { Meta = (TraceMeta)events[0], Events = events, Issues = [], LineCount = events.Count });
    }

    // ------------------------------------------------------------------ 1. timing is about the attempted input in the selected occurrence

    [Fact]
    public void TimingEvidenceCannotContradictTheVerifierWhenTheStateLastedLongButTheInputWasEarly()
    {
        // State 200 runs 22..33 (12 frames, more than the required 10), but step 2's input is sent at frame 25: 3 ticks in.
        var (plan, log) = Run(10, f => f is >= 22 and <= 33 ? 200 : f >= 34 ? 210 : 0, step2Input: 25, last: 60);
        var report = RouteVerifier.Verify(plan, log);
        Assert.Equal((VerifyStatus.Failed, VerifyReason.PreconditionNeverMet, 2), (report.Status, report.Reason, report.FailedStep));

        var ev = report.Steps[1].Evidence!;
        Assert.Equal((22L, 33L), (ev.SourceOccurrenceStartFrame, ev.SourceOccurrenceEndFrame));
        Assert.Equal(25, ev.InputAttemptFrame);
        Assert.Equal(3, ev.SourceTickAtInput);
        Assert.Equal(10, ev.RequiredEarliestTick);
        Assert.False(ev.TimingSatisfied);                                          // not "the state lasted 12 >= 10 ticks"
        Assert.Equal(PrerequisiteFailure.TimingNotSatisfied, ev.Failure);
        Assert.Equal(25, ev.FailureAnchorFrame);
        Assert.Contains("attempted too early", report.Steps[1].Detail ?? string.Empty);
    }

    [Fact]
    public void AnInputSentLateEnoughIsReportedAsSatisfiedAndIsNotTheFailure()
    {
        var (plan, log) = Run(2, f => f is >= 22 and <= 33 ? 200 : f >= 34 ? 210 : 0, step2Input: 25, last: 60);
        var ev = StepEvidenceBuilder.Build(plan.Steps[1], log.Frames.Where(f => f.Frame >= 20).ToList(), 2, log.Events.OfType<InputEvent>().Where(e => e.Step == 2 && e.Keys.Count > 0).ToList(),
            log.Events.OfType<DriverEvent>().ToList(), 60);
        Assert.Equal((3, true), (ev.SourceTickAtInput, ev.TimingSatisfied));
    }

    // ------------------------------------------------------------------ 2. a later repeated occurrence never replaces the original failure

    [Fact]
    public void ALaterRepeatedSourceOccurrenceCannotReplaceTheOriginalFailureOccurrence()
    {
        // State 200 runs 22..30 and ENDS (to State 0) at frame 31. The input only goes out at 33. State 200 is entered again at 60 and left for 210 at 71.
        var (plan, log) = Run(null, f => f is >= 22 and <= 30 ? 200 : f is >= 60 and <= 70 ? 200 : f >= 71 ? 210 : 0, step2Input: 33, last: 90);
        var report = RouteVerifier.Verify(plan, log);
        Assert.Equal((VerifyStatus.Failed, VerifyReason.WrongState, 2), (report.Status, report.Reason, report.FailedStep));

        var ev = report.Steps[1].Evidence!;
        Assert.Equal((0, 31L), (ev.FirstMismatchState, ev.FirstMismatchFrame));    // how the ORIGINAL occurrence ended — not the later 200 -> 210 at frame 71
        Assert.Equal((22L, 30L), (ev.SourceOccurrenceStartFrame, ev.SourceOccurrenceEndFrame));
        Assert.Equal(PrerequisiteFailure.DifferentMoveEntered, ev.Failure);
        Assert.Equal(31, ev.FailureAnchorFrame);
        Assert.True(ev.ExpectedStateEverObserved);                                 // true, but later, and it does not repair the failure ...
        Assert.Equal(71, ev.ExpectedStateFirstFrame);
        Assert.Contains("already ended at frame 31, before the input at frame 33", report.Steps[1].Detail);
        Assert.Contains("observed later, at frame 71", report.Steps[1].Detail);    // ... and the text says so instead of "never"
        Assert.Equal(31, PlaybackInspector.Inspect(report, log)!.FocusFrame);
    }

    // ------------------------------------------------------------------ 3. input telemetry vs source-occurrence telemetry (real Funny Valentine step 2)

    [Fact]
    public void ANoInputStepHasNoInputTelemetryButKeepsLabelledSourceOccurrenceTelemetry()
    {
        var plan = InputPlanner.FromJson(File.ReadAllText(Fx("m4_fv_plan.json")));
        var log = TraceReader.ReadFile(Fx("m4_fv_trace.jsonl"));
        var report = RouteVerifier.Verify(plan, log);
        var ev = report.Steps[1].Evidence!;
        Assert.False(ev.InputAttempted);
        Assert.Null(ev.InputAttemptFrame);
        Assert.Null(ev.SourceTickAtInput);
        Assert.Null(ev.SeparationAtInput);                                         // no "at input: 60" for a step that never had an input
        Assert.Equal("source-occurrence", ev.SeparationWindow);
        Assert.Equal((60.0, 59.0, 59.0, 60.0), (ev.SeparationAtSourceStart, ev.SeparationAtSourceEnd, ev.SeparationMin, ev.SeparationMax));
        Assert.True(ev.SourceStateObserved);
        Assert.False(ev.RequiredContactObserved);

        var diag = PlaybackDiagnostic.Build(new PlaybackDiagnostic.Source(1, AttemptState.VerdictProduced, null, report.RouteKey, null, null, null, Outcome(report, log, plan), 1));
        var rt = diag.Runtime!;
        Assert.Null(rt.PreInputTelemetry);
        Assert.Equal(274, rt.SourceOccurrenceStartTelemetry!.Frame);
        Assert.Equal(287, rt.SourceOccurrenceEndTelemetry!.Frame);
        Assert.Equal(60, rt.SourceOccurrenceStartTelemetry.DerivedDistance);
        Assert.Equal(59, rt.SourceOccurrenceEndTelemetry.DerivedDistance);
        Assert.Null(rt.Spacing.DerivedSeparationAtInput);
        using var doc = JsonDocument.Parse(diag.ToJson());
        var runtime = doc.RootElement.GetProperty("runtime");
        Assert.Equal(JsonValueKind.Null, runtime.GetProperty("preInputTelemetry").ValueKind);
        Assert.Equal(JsonValueKind.Null, runtime.GetProperty("spacing").GetProperty("derivedSeparationAtInput").ValueKind);
        Assert.Equal(274, runtime.GetProperty("sourceOccurrenceStartTelemetry").GetProperty("frame").GetInt64());
        Assert.Equal(287, runtime.GetProperty("failure").GetProperty("failureAnchorFrame").GetInt64());
    }

    private static PlaybackOutcome Outcome(VerificationReport report, TraceLog log, InputPlan plan) =>
        new(new PlaybackRecord("run-1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), report.Character, report.RouteKey, "x", report.Status.ToString(), report.Reason, report.FailedStep,
            "kfm", "stages/kfm.def", report.EngineSha256, 1, Path.GetTempPath()), report, log) { Plan = plan };

    [Fact]
    public void AStepWithAnInputKeepsInputTelemetryAtTheInputFrame()
    {
        var (plan, log) = Run(10, f => f is >= 22 and <= 33 ? 200 : f >= 34 ? 210 : 0, step2Input: 25, last: 60);
        var report = RouteVerifier.Verify(plan, log);
        var diag = PlaybackDiagnostic.Build(new PlaybackDiagnostic.Source(1, AttemptState.VerdictProduced, null, report.RouteKey, null, null, null, Outcome(report, log, plan), 1));
        Assert.Equal(25, diag.Runtime!.PreInputTelemetry!.Frame);
        Assert.NotNull(diag.Runtime.Spacing.DerivedSeparationAtInput);
    }

    // ------------------------------------------------------------------ the real Goku trace (when it has been added) — facts the trace itself supports

    [Fact]
    public void TheRealGokuTraceIfPresentPreservesTheFirstWrongStateAtFrame291()
    {
        var path = Fx("m4_goku_real_trace.jsonl");
        if (!File.Exists(path)) return;   // the real trace was not available when this pass was made; the reconstructed fixture covers the reported facts meanwhile
        var log = TraceReader.ReadFile(path);
        var frames = log.Frames.ToList();
        Assert.Contains(log.Events.OfType<InputEvent>(), e => e.Frame == 290 && e.Keys.SequenceEqual(["a"]));
        var first = frames.Zip(frames.Skip(1)).First(p => p.First.P1.State == 0 && p.Second.P1.State == 200 && p.Second.Frame >= 290);
        Assert.Equal(291, first.Second.Frame);
        Assert.Equal("A", first.Second.P1.MoveType);
        Assert.DoesNotContain(frames, f => f.P1.State == 17200);                   // the expected state never occurred
        Assert.Contains(log.Events.OfType<DriverEvent>(), d => d.Kind == "step_timeout" || d.Kind == "timeout");
    }
}
