using System.Globalization;
using IKEMENLab.Core.XRay.Runtime;

namespace IKEMENLab.Core.XRay.Verify;

/// <summary>Why a step's prerequisites were not met, from trace evidence (never from the driver's final timeout string alone).</summary>
public static class PrerequisiteFailure
{
    public const string SourceStateNeverObserved = "SourceStateNeverObserved";
    public const string RequiredContactNotObserved = "RequiredContactNotObserved";
    public const string TimingNotSatisfied = "TimingNotSatisfied";
    public const string InputNotAttempted = "InputNotAttempted";
    public const string DifferentMoveEntered = "DifferentMoveEntered";
    public const string ExpectedStateNeverObserved = "ExpectedStateNeverObserved";
    /// <summary>The expected transition happened before the planned input (the verifier reports WrongState).</summary>
    public const string TransitionPrecededInput = "TransitionPrecededInput";
    /// <summary>An input was attempted but no decisive transition happened inside the judged attempt window (the verifier reports TransitionNotObserved).</summary>
    public const string NoTransitionBeforeDeadline = "NoTransitionBeforeDeadline";
    /// <summary>The samples ended (with no end marker) before the judged deadline, so nothing can be said about the rest of the attempt.</summary>
    public const string TelemetryEndedBeforeDeadline = "TelemetryEndedBeforeDeadline";
}

/// <summary>
/// Structured, trace-derived facts about one failed step, anchored to the SAME source occurrence and attempt window the verifier judged: the occurrence that
/// began where the preceding step's transition was observed (for a step with a source state), and the frames up to the verifier's own attempt limit.
/// Every field is a measurement of recorded frames or a quote of what the driver reported; none says which controller ran. A null means "not applicable or not
/// measurable", never "false".
/// </summary>
public sealed record StepEvidence(
    int? ExpectedState,
    int? SourceState,
    bool? SourceStateObserved,
    /// <summary>First and last frame of the verifier-selected source occurrence (null for a step that starts from neutral, or when the source never occurred).</summary>
    long? SourceOccurrenceStartFrame,
    long? SourceOccurrenceEndFrame,
    /// <summary>How the occurrence stopped being observed: "Exit" (a later sample inside the judged window shows another state), "Deadline" (still in the source state when the judged window ended — a boundary, NOT an observed exit), "TraceEnd" (the samples ended inside the window), or null.</summary>
    string? SourceOccurrenceEndKind,
    string? RequiredContact,
    bool? RequiredContactObserved,
    int? RequiredEarliestTick,
    /// <summary>Ticks the source occurrence had run when the input was attempted (input frame − occurrence start). Null when no input was attempted or there is no source occurrence.</summary>
    int? SourceTickAtInput,
    /// <summary>Was the ATTEMPTED input late enough within the selected occurrence (SourceTickAtInput ≥ RequiredEarliestTick)? Not "did the state last long enough". Null without an attempted input or a requirement.</summary>
    bool? TimingSatisfied,
    bool InputAttempted,
    long? InputAttemptFrame,
    IReadOnlyList<string> AttemptedKeys,
    int? FirstMismatchState,
    long? FirstMismatchFrame,
    bool ExpectedStateEverObserved,
    long? ExpectedStateFirstFrame,
    /// <summary>The frames the verifier judged for this step: from the start of the source occurrence (or the previous transition) to its attempt deadline. Evidence never looks beyond them.</summary>
    long? JudgedAttemptStartFrame,
    long? JudgedAttemptEndFrame,
    /// <summary>The last sampled frame inside the judged window.</summary>
    long? ObservedThroughFrame,
    /// <summary>True when the samples cover the whole judged window, or the trace carries an end marker. False = the evidence is incomplete: absence of an event is not shown.</summary>
    bool ObservationComplete,
    /// <summary>First frame inside the judged window where P1 entered the expected state (null when it did not, even if it occurs later in the trace).</summary>
    long? ExpectedTargetFirstFrame,
    /// <summary>The expected transition happened at or before the attempted input (the premature transition). Null when not applicable.</summary>
    bool? TransitionPrecededInput,
    /// <summary>What <see cref="FailureAnchorFrame"/> is: FirstMismatch | PrematureTransition | SourceOccurrenceEnd | InputAttempt | AttemptDeadline.</summary>
    string? FailureAnchorKind,
    string? DriverClaim,
    long? DriverClaimFrame,
    /// <summary>The frame where the failure is decided: the first mismatch, else the end of the source occurrence (missing contact / source), else the input frame. Null when none applies.</summary>
    long? FailureAnchorFrame,
    int ConfiguredApproachDistance,
    /// <summary>Derived separation at the attempted input. Null when no input was attempted — source-occurrence telemetry is never repurposed as input telemetry.</summary>
    double? SeparationAtInput,
    double? SeparationAtSourceStart,
    double? SeparationAtSourceEnd,
    /// <summary>What the min/max below span: "source-occurrence", "attempt-window" (neutral start: from the input to the verifier's attempt limit), or null.</summary>
    string? SeparationWindow,
    double? SeparationMin,
    double? SeparationMax,
    IReadOnlyList<string> SeparationSources,
    string? Failure)
{
    /// <summary>Executed-controller identity is not part of the trace: observing a destination state does not establish which controller produced it.</summary>
    public string ExecutedController => "unknown";
}

public static class StepEvidenceBuilder
{
    /// <summary>
    /// <paramref name="cursor"/> is the verifier's index of the frame where the preceding step's transition was observed — the start of this step's source occurrence.
    /// </summary>
    public static StepEvidence Build(PlanStep step, IReadOnlyList<FrameEvent> frames, int cursor, IReadOnlyList<InputEvent> stepInputs,
        IReadOnlyList<DriverEvent> driver, int approachDistance, bool traceEnded = true)
    {
        cursor = Math.Clamp(cursor, 0, Math.Max(0, frames.Count - 1));
        var inputFrame = stepInputs.Select(e => (long?)e.Frame).FirstOrDefault();
        var keys = stepInputs.FirstOrDefault()?.Keys ?? [];

        // The verifier's own window for this step: from the cursor to input + command length + timeout (or cursor + 180 + timeout without an input).
        // Everything below — the occurrence, contact, separation, anchors — is confined to it; nothing after the deadline can change the diagnosis.
        var judgedStart = frames[cursor].Frame;
        var judgedEnd = inputFrame is { } first ? first + step.Input.Count + step.TimeoutFrames : judgedStart + 180 + step.TimeoutFrames;
        var lastSample = frames.Count > 0 ? frames[^1].Frame : judgedStart;
        var observedThrough = Math.Min(lastSample, judgedEnd);
        var complete = lastSample >= judgedEnd || traceEnded;

        // The source occurrence: the contiguous run of frames in the source state starting at the cursor, up to the judged deadline.
        var occurrence = step.FromState is { } src
            ? frames.Skip(cursor).TakeWhile(f => f.P1.State == src && f.Frame <= judgedEnd).ToList()
            : [];
        bool? sourceObserved = step.FromState is null ? null : occurrence.Count > 0;
        long? occStart = occurrence.Count > 0 ? occurrence[0].Frame : null;
        long? occEnd = occurrence.Count > 0 ? occurrence[^1].Frame : null;
        string? occEndKind = null;
        if (occurrence.Count > 0)
        {
            var next = cursor + occurrence.Count < frames.Count ? frames[cursor + occurrence.Count] : null;
            occEndKind = next is { } n && n.Frame <= judgedEnd && n.P1.State != step.FromState ? "Exit"
                : lastSample >= judgedEnd || next is not null ? "Deadline" : "TraceEnd";
        }

        bool? contactObserved = null;
        if (step.Contact is { } need && occurrence.Count > 0)
        {
            var missing = occurrence.Any(f => f.P1.MoveHit is null || f.P1.MoveContact is null);
            if (!missing)
            {
                var reset = false;
                var seen = false;
                for (var i = 0; i < occurrence.Count; i++)
                {
                    var sample = occurrence[i].P1;
                    if (!Contacted(sample, need)) reset = true;
                    var idx = cursor + i;
                    var fresh = idx > 0 && frames[idx].P2.Life < frames[idx - 1].P2.Life;
                    if (Contacted(sample, need) && (reset || fresh)) { seen = true; break; }
                }

                contactObserved = seen ? true : complete ? false : null;   // not seen in an INCOMPLETE trace is unknown, not absent
            }
        }

        // Timing is about the input that was actually sent, measured from the start of the selected occurrence (as the verifier does).
        int? tickAtInput = inputFrame is { } inf && occStart is { } os ? (int)(inf - os) : null;
        bool? timing = step.EarliestTick is { } minTick && tickAtInput is { } t ? t >= minTick : null;

        var inWindow = frames.Skip(cursor + 1).TakeWhile(f => f.Frame <= judgedEnd).ToList();
        var (mismatchState, mismatchFrame) = FirstMismatch(step, frames, cursor, inputFrame, judgedEnd);
        var expected = frames.Skip(cursor).FirstOrDefault(f => f.P1.State == step.ToState);               // anywhere in the trace (informational)
        var expectedInWindow = inWindow.FirstOrDefault(f => f.P1.State == step.ToState)?.Frame;           // inside the judged window only
        var claim = driver.LastOrDefault(d => d.Step == step.Index && d.Kind is "timeout" or "step_timeout");

        // The expected transition itself, for a step with a source state: the first state change of the occurrence, if it is into the target and not after the input.
        bool? precededInput = null;
        if (step.FromState is not null && inputFrame is { } pin)
        {
            var firstChange = inWindow.FirstOrDefault(f => f.P1.State != frames[cursor].P1.State);
            precededInput = firstChange is { } fc && fc.P1.State == step.ToState && fc.Frame <= pin;
        }

        // Separation: at the input only if an input happened; otherwise the source occurrence (or attempt window) is reported under its own label.
        double? atInput = inputFrame is { } ip ? frames.LastOrDefault(f => f.Frame <= ip && f.Distance is not null)?.Distance : null;
        List<FrameEvent> window;
        string? windowKind;
        if (occurrence.Count > 0) { window = occurrence; windowKind = "source-occurrence"; }
        else if (inputFrame is { } w0)
        {
            var end = Math.Min(judgedEnd, mismatchFrame ?? long.MaxValue);
            window = frames.Where(f => f.Frame >= w0 && f.Frame <= end).ToList();
            windowKind = "attempt-window";
        }
        else { window = []; windowKind = null; }
        var measured = window.Where(f => f.Distance is not null).ToList();
        var sources = window.Where(f => f.DistanceSource is not null).Select(f => f.DistanceSource!).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        string? failure = null;
        // Hard facts first: an attempted input that led to a different move, or an expected transition that came before the input, or a measured early input.
        if (inputFrame is not null && mismatchState is not null) failure = PrerequisiteFailure.DifferentMoveEntered;
        else if (precededInput == true) failure = PrerequisiteFailure.TransitionPrecededInput;
        else if (timing == false) failure = PrerequisiteFailure.TimingNotSatisfied;
        // Then incompleteness: samples that stop before the deadline cannot show that something did NOT happen.
        else if (!complete) failure = PrerequisiteFailure.TelemetryEndedBeforeDeadline;
        else if (step.FromState is not null && sourceObserved == false) failure = PrerequisiteFailure.SourceStateNeverObserved;
        else if (step.Contact is not null && contactObserved == false) failure = PrerequisiteFailure.RequiredContactNotObserved;
        else if (inputFrame is null) failure = PrerequisiteFailure.InputNotAttempted;
        else if (expectedInWindow is null) failure = PrerequisiteFailure.NoTransitionBeforeDeadline;

        (long? anchor, string? kind) = failure switch
        {
            PrerequisiteFailure.DifferentMoveEntered => (mismatchFrame, "FirstMismatch"),
            PrerequisiteFailure.TransitionPrecededInput => (expectedInWindow, "PrematureTransition"),
            PrerequisiteFailure.RequiredContactNotObserved or PrerequisiteFailure.SourceStateNeverObserved =>
                (occEnd ?? inputFrame, occEnd is null ? "InputAttempt" : occEndKind == "Exit" ? "SourceOccurrenceEnd" : "SourceOccurrenceDeadline"),
            PrerequisiteFailure.TimingNotSatisfied => (inputFrame, "InputAttempt"),
            PrerequisiteFailure.InputNotAttempted => (occEnd, occEnd is null ? null : occEndKind == "Exit" ? "SourceOccurrenceEnd" : "SourceOccurrenceDeadline"),
            PrerequisiteFailure.NoTransitionBeforeDeadline => (judgedEnd, "AttemptDeadline"),
            PrerequisiteFailure.TelemetryEndedBeforeDeadline => (observedThrough, "TraceEnd"),
            _ => (null, null)
        };

        return new StepEvidence(step.ToState, step.FromState, sourceObserved, occStart, occEnd, occEndKind, step.Contact, contactObserved, step.EarliestTick, tickAtInput, timing,
            inputFrame is not null, inputFrame, keys.ToList(), mismatchState, mismatchFrame, expected is not null, expected?.Frame,
            judgedStart, judgedEnd, observedThrough, complete, expectedInWindow, precededInput, kind, claim?.Detail, claim?.Frame, anchor,
            approachDistance, atInput, occurrence.Count > 0 ? occurrence[0].Distance : null, occurrence.Count > 0 ? occurrence[^1].Distance : null, windowKind,
            measured.Count == 0 ? null : measured.Min(f => f.Distance), measured.Count == 0 ? null : measured.Max(f => f.Distance), sources, failure);
    }

    /// <summary>
    /// The first decisive wrong move within the verifier's judged attempt (<paramref name="limit"/> is its deadline; nothing later is considered), and only when an input was
    /// attempted. For a step with a source state it is how that source occurrence ENDED (its first exit — before the input counts; a later repeated occurrence never replaces it);
    /// an exit INTO the expected state is not a mismatch (a premature expected transition is reported separately). For a neutral start it is the first attack/control-loss after
    /// the input. Found once; a later return to neutral or a driver timeout never replaces it.
    /// </summary>
    public static (int? State, long? Frame) FirstMismatch(PlanStep step, IReadOnlyList<FrameEvent> frames, int cursor, long? inputFrame, long? limit = null)
    {
        if (inputFrame is null) return (null, null);
        for (var i = Math.Max(cursor + 1, 1); i < frames.Count; i++)
        {
            if (limit is { } l && frames[i].Frame > l) return (null, null);
            var prev = frames[i - 1].P1;
            var cur = frames[i].P1;
            if (cur.State == prev.State) continue;
            if (step.FromState is { } from)
            {
                // The first state change after the cursor is the end of the source occurrence; there is no later chance to match.
                if (prev.State != from) return (null, null);
                return cur.State == step.ToState ? (null, null) : (cur.State, frames[i].Frame);
            }

            if (frames[i].Frame <= inputFrame) continue;
            if (cur.State == step.ToState) return (null, null);
            if (cur.MoveType is "A" or "Attack" || (prev.Ctrl == true && cur.Ctrl == false)) return (cur.State, frames[i].Frame);
        }

        return (null, null);
    }

    private static bool Contacted(PlayerSample p, string need) => need switch
    {
        "hit" => p.MoveHit is > 0,
        "contact" => p.MoveHit is > 0 || p.MoveContact is > 0,
        "guarded" => p.MoveContact is > 0 && p.MoveHit is 0,
        _ => false
    };
}

/// <summary>Plain-language statements of what the evidence shows, with nothing beyond it (no claim about which controller ran).</summary>
public static class FailureNarrative
{
    /// <summary>Null when the evidence has no sharper statement than the generic detail.</summary>
    public static string? Describe(PlanStep step, StepEvidence e, string reason)
    {
        var keys = string.Join("+", e.AttemptedKeys);
        switch (e.Failure)
        {
            case PrerequisiteFailure.RequiredContactNotObserved when e.SourceState is { } src:
            {
                var lead = step.Index == 2 ? "the opening attack" : "the previous attack";
                var attempted = e.InputAttempted ? $"Step {step.Index} was attempted, but {lead} did not connect." : $"Step {step.Index} was not attempted: {lead} did not connect.";
                return $"{attempted} State {src} occurred (frames {e.SourceOccurrenceStartFrame}–{e.SourceOccurrenceEndFrame}{(e.SourceOccurrenceEndKind == "Deadline" ? "; still in that state when the judged attempt ended at the deadline, not an observed exit" : string.Empty)}), but the required contact ({e.RequiredContact}) was not observed within the judged attempt.";
            }
            case PrerequisiteFailure.SourceStateNeverObserved when e.SourceState is { } src2:
                return $"Step {step.Index} {(e.InputAttempted ? "was attempted" : "was not attempted")}: State {src2}, the state it starts from, was never observed.";
            case PrerequisiteFailure.TransitionPrecededInput when e.SourceState is { } ps:
                return $"Step {step.Index} failed: the expected transition (State {ps} → {e.ExpectedState}) happened at frame {e.ExpectedTargetFirstFrame}, before the planned input at frame {e.InputAttemptFrame}.";
            case PrerequisiteFailure.TelemetryEndedBeforeDeadline:
                return $"Step {step.Index} could not be judged: the samples ended at frame {e.ObservedThroughFrame}, before the judged attempt deadline (frame {e.JudgedAttemptEndFrame}), and no end marker was recorded. Nothing is claimed about the rest of the attempt.";
            case PrerequisiteFailure.NoTransitionBeforeDeadline:
                return $"Step {step.Index} was attempted at frame {e.InputAttemptFrame}, but State {e.ExpectedState} was not entered by the end of the judged attempt (frame {e.JudgedAttemptEndFrame}).";
            case PrerequisiteFailure.TimingNotSatisfied:
                return $"Step {step.Index} was attempted too early: its input was sent {e.SourceTickAtInput} tick(s) into State {e.SourceState}, but the transition needs tick {e.RequiredEarliestTick} or later.";
            default:
                if (reason == VerifyReason.WrongState && e.FirstMismatchState is { } m2)
                    return $"Step {step.Index} failed: different move. Expected State {e.ExpectedState}. Observed State {m2} at frame {e.FirstMismatchFrame}" +
                           (keys.Length > 0 ? $" after the reported {keys} input." : ".") +
                           (e.InputAttemptFrame is { } inf && e.FirstMismatchFrame is { } mf && mf < inf ? $" (the source move had already ended at frame {mf}, before the input at frame {inf}.)" : string.Empty) +
                           (e.ExpectedStateEverObserved ? $" State {e.ExpectedState} was observed later, at frame {e.ExpectedStateFirstFrame}." : $" State {e.ExpectedState} was never observed.");
                return null;
        }
    }

    public static string Frame(long? f) => f?.ToString(CultureInfo.InvariantCulture) ?? "?";
}
