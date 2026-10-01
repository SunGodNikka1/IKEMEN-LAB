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
        IReadOnlyList<DriverEvent> driver, int approachDistance)
    {
        cursor = Math.Clamp(cursor, 0, Math.Max(0, frames.Count - 1));
        var inputFrame = stepInputs.Select(e => (long?)e.Frame).FirstOrDefault();
        var keys = stepInputs.FirstOrDefault()?.Keys ?? [];

        // The source occurrence: the contiguous run of frames in the source state starting at the cursor. Nothing later in the trace belongs to it.
        var occurrence = step.FromState is { } src
            ? frames.Skip(cursor).TakeWhile(f => f.P1.State == src).ToList()
            : [];
        bool? sourceObserved = step.FromState is null ? null : occurrence.Count > 0;
        long? occStart = occurrence.Count > 0 ? occurrence[0].Frame : null;
        long? occEnd = occurrence.Count > 0 ? occurrence[^1].Frame : null;

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

                contactObserved = seen;
            }
        }

        // Timing is about the input that was actually sent, measured from the start of the selected occurrence (as the verifier does).
        int? tickAtInput = inputFrame is { } inf && occStart is { } os ? (int)(inf - os) : null;
        bool? timing = step.EarliestTick is { } minTick && tickAtInput is { } t ? t >= minTick : null;

        var limit = inputFrame is { } first ? first + step.Input.Count + step.TimeoutFrames : (long?)null;
        var (mismatchState, mismatchFrame) = FirstMismatch(step, frames, cursor, inputFrame, limit);
        var expected = frames.Skip(cursor).FirstOrDefault(f => f.P1.State == step.ToState);
        var claim = driver.LastOrDefault(d => d.Step == step.Index && d.Kind is "timeout" or "step_timeout");

        // Separation: at the input only if an input happened; otherwise the source occurrence (or attempt window) is reported under its own label.
        double? atInput = inputFrame is { } ip ? frames.LastOrDefault(f => f.Frame <= ip && f.Distance is not null)?.Distance : null;
        List<FrameEvent> window;
        string? windowKind;
        if (occurrence.Count > 0) { window = occurrence; windowKind = "source-occurrence"; }
        else if (inputFrame is { } w0)
        {
            var end = Math.Min(limit ?? w0, mismatchFrame ?? long.MaxValue);
            window = frames.Where(f => f.Frame >= w0 && f.Frame <= end).ToList();
            windowKind = "attempt-window";
        }
        else { window = []; windowKind = null; }
        var measured = window.Where(f => f.Distance is not null).ToList();
        var sources = window.Where(f => f.DistanceSource is not null).Select(f => f.DistanceSource!).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        string? failure = null;
        // An attempted input that led to a different move is the decisive fact, whatever the contact flags say.
        if (inputFrame is not null && mismatchState is not null) failure = PrerequisiteFailure.DifferentMoveEntered;
        else if (step.FromState is not null && sourceObserved == false) failure = PrerequisiteFailure.SourceStateNeverObserved;
        else if (step.Contact is not null && contactObserved == false) failure = PrerequisiteFailure.RequiredContactNotObserved;
        else if (timing == false) failure = PrerequisiteFailure.TimingNotSatisfied;
        else if (inputFrame is null) failure = PrerequisiteFailure.InputNotAttempted;
        else if (expected is null) failure = PrerequisiteFailure.ExpectedStateNeverObserved;

        long? anchor = failure switch
        {
            PrerequisiteFailure.DifferentMoveEntered => mismatchFrame,
            PrerequisiteFailure.RequiredContactNotObserved or PrerequisiteFailure.SourceStateNeverObserved => occEnd ?? inputFrame,
            PrerequisiteFailure.TimingNotSatisfied => inputFrame,
            PrerequisiteFailure.InputNotAttempted => occEnd,
            _ => mismatchFrame ?? inputFrame
        };

        return new StepEvidence(step.ToState, step.FromState, sourceObserved, occStart, occEnd, step.Contact, contactObserved, step.EarliestTick, tickAtInput, timing,
            inputFrame is not null, inputFrame, keys.ToList(), mismatchState, mismatchFrame, expected is not null, expected?.Frame, claim?.Detail, claim?.Frame, anchor,
            approachDistance, atInput, occurrence.Count > 0 ? occurrence[0].Distance : null, occurrence.Count > 0 ? occurrence[^1].Distance : null, windowKind,
            measured.Count == 0 ? null : measured.Min(f => f.Distance), measured.Count == 0 ? null : measured.Max(f => f.Distance), sources, failure);
    }

    /// <summary>
    /// The first decisive wrong move within the verifier's own attempt: for a step with a source state, how that source occurrence ENDED (its first exit — exits before the
    /// input count, a later repeated occurrence of the same state never replaces it); for a neutral start, the first attack/control-loss after the input up to the verifier's
    /// attempt limit. Found once; a later return to neutral or a driver timeout never replaces it.
    /// </summary>
    public static (int? State, long? Frame) FirstMismatch(PlanStep step, IReadOnlyList<FrameEvent> frames, int cursor, long? inputFrame, long? limit = null)
    {
        for (var i = Math.Max(cursor + 1, 1); i < frames.Count; i++)
        {
            var prev = frames[i - 1].P1;
            var cur = frames[i].P1;
            if (cur.State == prev.State) continue;
            if (step.FromState is { } from)
            {
                // The first state change after the cursor is the end of the source occurrence; there is no later chance to match.
                if (prev.State != from) return (null, null);
                return cur.State == step.ToState ? (null, null) : (cur.State, frames[i].Frame);
            }

            if (inputFrame is null || frames[i].Frame <= inputFrame) continue;
            if (limit is { } l && frames[i].Frame > l) return (null, null);
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
                return $"{attempted} State {src} occurred (frames {e.SourceOccurrenceStartFrame}–{e.SourceOccurrenceEndFrame}), but the required contact ({e.RequiredContact}) was never observed.";
            }
            case PrerequisiteFailure.SourceStateNeverObserved when e.SourceState is { } src2:
                return $"Step {step.Index} {(e.InputAttempted ? "was attempted" : "was not attempted")}: State {src2}, the state it starts from, was never observed.";
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
