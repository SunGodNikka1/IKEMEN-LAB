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
/// Structured, trace-derived facts about one failed step. Every field is a measurement of the recorded frames or a quote of what the driver reported;
/// none of it says which controller ran (the trace does not carry that), and a null means "not applicable or not measurable", never "false".
/// </summary>
public sealed record StepEvidence(
    int? ExpectedState,
    int? SourceState,
    bool? SourceStateObserved,
    long? SourceStateFirstFrame,
    long? SourceStateLastFrame,
    string? RequiredContact,
    bool? RequiredContactObserved,
    int? EarliestTick,
    bool? TimingSatisfied,
    bool InputAttempted,
    long? FirstInputFrame,
    IReadOnlyList<string> AttemptedKeys,
    int? FirstMismatchState,
    long? FirstMismatchFrame,
    bool ExpectedStateEverObserved,
    long? ExpectedStateFirstFrame,
    string? DriverClaim,
    long? DriverClaimFrame,
    int ConfiguredApproachDistance,
    double? SeparationAtInput,
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
    public static StepEvidence Build(PlanStep step, IReadOnlyList<FrameEvent> frames, int cursor, IReadOnlyList<InputEvent> stepInputs,
        IReadOnlyList<DriverEvent> driver, int approachDistance)
    {
        cursor = Math.Clamp(cursor, 0, Math.Max(0, frames.Count - 1));
        var inputFrame = stepInputs.Select(e => (long?)e.Frame).FirstOrDefault();
        var keys = stepInputs.FirstOrDefault()?.Keys ?? [];

        // The source occurrence is the run of frames P1 spent in the source state, starting where the previous step put it.
        var occurrence = step.FromState is { } src
            ? frames.Skip(cursor).TakeWhile(f => f.P1.State == src).ToList()
            : [];
        bool? sourceObserved = step.FromState is null ? null : occurrence.Count > 0;

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

        bool? timing = step.EarliestTick is { } minTick && occurrence.Count > 0 ? occurrence.Count > minTick : null;

        var (mismatchState, mismatchFrame) = FirstMismatch(step, frames, cursor, inputFrame);
        var expected = frames.Skip(cursor).FirstOrDefault(f => f.P1.State == step.ToState);
        var claim = driver.LastOrDefault(d => d.Step == step.Index && d.Kind is "timeout" or "step_timeout");

        // Separation is derived telemetry (see its source); it is reported around the input, separately from the configured approach threshold.
        var windowStart = inputFrame ?? frames[cursor].Frame;
        var windowEnd = occurrence.Count > 0 ? occurrence[^1].Frame : windowStart + 30;
        var window = frames.Where(f => f.Frame >= windowStart && f.Frame <= windowEnd && f.Distance is not null).ToList();
        double? atInput = frames.LastOrDefault(f => f.Frame <= windowStart && f.Distance is not null)?.Distance;
        var sources = frames.Where(f => f.Frame >= windowStart - 1 && f.Frame <= windowEnd && f.DistanceSource is not null).Select(f => f.DistanceSource!).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

        string? failure = null;
        // An attempted input that led to a different move is the decisive fact, whatever the contact flags say.
        if (inputFrame is not null && mismatchState is not null && expected is null) failure = PrerequisiteFailure.DifferentMoveEntered;
        else if (step.FromState is not null && sourceObserved == false) failure = PrerequisiteFailure.SourceStateNeverObserved;
        else if (step.Contact is not null && contactObserved == false) failure = PrerequisiteFailure.RequiredContactNotObserved;
        else if (timing == false) failure = PrerequisiteFailure.TimingNotSatisfied;
        else if (inputFrame is null) failure = PrerequisiteFailure.InputNotAttempted;
        else if (expected is null) failure = PrerequisiteFailure.ExpectedStateNeverObserved;

        return new StepEvidence(step.ToState, step.FromState, sourceObserved, occurrence.Count > 0 ? occurrence[0].Frame : null,
            occurrence.Count > 0 ? occurrence[^1].Frame : null, step.Contact, contactObserved, step.EarliestTick, timing,
            inputFrame is not null, inputFrame, keys.ToList(), mismatchState, mismatchFrame, expected is not null, expected?.Frame,
            claim?.Detail, claim?.Frame, approachDistance, atInput, window.Count == 0 ? null : window.Min(f => f.Distance),
            window.Count == 0 ? null : window.Max(f => f.Distance), sources, failure);
    }

    /// <summary>
    /// The first decisive wrong move after the input: P1 leaving neutral (or the source occurrence) for a state that is not the expected one. It is found
    /// once, at the first such frame; a later return to neutral or a driver timeout never replaces it.
    /// </summary>
    public static (int? State, long? Frame) FirstMismatch(PlanStep step, IReadOnlyList<FrameEvent> frames, int cursor, long? inputFrame)
    {
        if (inputFrame is null) return (null, null);
        for (var i = Math.Max(cursor + 1, 1); i < frames.Count; i++)
        {
            var prev = frames[i - 1].P1;
            var cur = frames[i].P1;
            if (cur.State == prev.State || frames[i].Frame <= inputFrame) continue;
            if (cur.State == step.ToState) return (null, null);
            if (step.FromState is null)
            {
                if (cur.MoveType is "A" or "Attack" || (prev.Ctrl == true && cur.Ctrl == false)) return (cur.State, frames[i].Frame);
                continue;
            }

            if (prev.State == step.FromState) return (cur.State, frames[i].Frame);
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
                return $"{attempted} State {src} occurred (frames {e.SourceStateFirstFrame}–{e.SourceStateLastFrame}), but the required contact ({e.RequiredContact}) was never observed.";
            }
            case PrerequisiteFailure.SourceStateNeverObserved when e.SourceState is { } src2:
                return $"Step {step.Index} {(e.InputAttempted ? "was attempted" : "was not attempted")}: State {src2}, the state it starts from, was never observed.";
            case PrerequisiteFailure.TimingNotSatisfied:
                return $"Step {step.Index} {(e.InputAttempted ? "was attempted" : "was not attempted")}: the source state ended before tick {e.EarliestTick}, the earliest the transition can occur.";
            case PrerequisiteFailure.DifferentMoveEntered when reason == VerifyReason.WrongState && e.FirstMismatchState is { } mm:
                return $"Step {step.Index} failed: different move. Expected State {e.ExpectedState}. Observed State {mm} at frame {e.FirstMismatchFrame}" +
                       (keys.Length > 0 ? $" after the reported {keys} input." : ".") + $" State {e.ExpectedState} was never observed.";
            default:
                if (reason == VerifyReason.WrongState && e.FirstMismatchState is { } m2)
                    return $"Step {step.Index} failed: different move. Expected State {e.ExpectedState}. Observed State {m2} at frame {e.FirstMismatchFrame}" +
                           (keys.Length > 0 ? $" after the reported {keys} input." : ".") +
                           (e.ExpectedStateEverObserved ? $" State {e.ExpectedState} was observed later, at frame {e.ExpectedStateFirstFrame}." : $" State {e.ExpectedState} was never observed.");
                return null;
        }
    }

    public static string Frame(long? f) => f?.ToString(CultureInfo.InvariantCulture) ?? "?";
}
