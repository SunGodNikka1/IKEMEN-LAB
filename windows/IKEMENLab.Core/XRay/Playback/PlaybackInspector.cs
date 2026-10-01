using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

/// <summary>One tick of a trace laid out for reading: both fighters' raw facts plus what the driver did that tick.</summary>
public sealed record TraceRow(
    long Frame, int? P1State, string? P1MoveType, bool? P1Ctrl, int? P1MoveHit, int? P2State, string? P2MoveType, double? P2Life, double? Distance, string Notes);

/// <summary>Everything the app needs to show "why did this fail" for one run, derived only from the verdict and the trace.</summary>
public sealed record FailureInspection(
    string Headline, string Why, IReadOnlyList<string> Hints, int? StepIndex, string? EdgeId, string? FromStateId, string? ToStateId,
    long? FocusFrame, IReadOnlyList<TraceRow> Window);

public static class PlaybackInspector
{
    /// <summary>The whole trace as rows; input and driver events are folded into the Notes of the tick they happened on.</summary>
    public static IReadOnlyList<TraceRow> Timeline(TraceLog log, int cap = 6000)
    {
        var notesByFrame = new Dictionary<long, List<string>>();
        void Add(long frame, string text)
        {
            if (!notesByFrame.TryGetValue(frame, out var l)) notesByFrame[frame] = l = [];
            l.Add(text);
        }

        foreach (var e in log.Events)
        {
            switch (e)
            {
                case InputEvent i when i.Player == 1:
                    Add(i.Frame, "input " + (i.Keys.Count == 0 ? "release" : string.Join("+", i.Keys)) + (i.Step is { } s ? $" (step {s})" : string.Empty));
                    break;
                case DriverEvent d:
                    Add(d.Frame, d.Kind + (d.Step is { } ds ? $" (step {ds})" : string.Empty) + (string.IsNullOrEmpty(d.Detail) ? string.Empty : $": {d.Detail}"));
                    break;
                case EndEvent end:
                    Add(end.Frame, "end" + (end.Reason is null ? string.Empty : ": " + end.Reason));
                    break;
            }
        }

        var rows = new List<TraceRow>();
        foreach (var f in log.Frames.Take(cap))
        {
            notesByFrame.TryGetValue(f.Frame, out var notes);
            rows.Add(new TraceRow(f.Frame, f.P1.State, f.P1.MoveType, f.P1.Ctrl, f.P1.MoveHit, f.P2.State, f.P2.MoveType, f.P2.Life, f.Distance,
                notes is null ? string.Empty : string.Join(" · ", notes)));
        }

        return rows;
    }

    /// <summary>Explains a Failed or Inconclusive report in plain terms and cuts the trace to the frames around the failure. Verified reports return null.</summary>
    public static FailureInspection? Inspect(VerificationReport report, TraceLog log, int radius = 20)
    {
        if (report.Status == VerifyStatus.Verified) return null;

        var step = report.FailedStep is { } n ? report.Steps.FirstOrDefault(s => s.Index == n) : null;
        var focus = FocusFrame(report, step, log);
        var all = Timeline(log);
        var window = focus is { } f
            ? all.Where(r => r.Frame >= f - radius && r.Frame <= f + radius).ToList()
            : all.Take(radius * 2).ToList();

        var why = step?.Detail ?? report.Continuity.Detail ?? report.Notes.FirstOrDefault() ?? "The run could not be judged.";
        var headline = report.Status == VerifyStatus.Inconclusive
            ? "Could not be tested: " + Describe(report.Reason)
            : step is not null && report.Reason != VerifyReason.ComboDropped
                ? $"Failed at step {step.Index}: {Describe(report.Reason)}"
                : "Failed: " + Describe(report.Reason);

        return new FailureInspection(headline, why, Hints(report.Reason), step?.Index, step?.EdgeId, step?.FromId, step?.ToId, focus, window);
    }

    private static long? FocusFrame(VerificationReport report, StepVerdict? step, TraceLog log)
    {
        if (report.Reason == VerifyReason.ComboDropped && report.Continuity.DropFrame is { } drop) return drop;
        if (step is { } s)
        {
            if (s.TransitionFrame is { } t) return t;
            if (s.InputFrame is { } i) return i;
        }

        // The last moment the driver reported something about the failed step, else the last driver event at all.
        var driver = log.Events.OfType<DriverEvent>().Where(d => step is null || d.Step == step.Index).LastOrDefault()
                     ?? log.Events.OfType<DriverEvent>().LastOrDefault();
        return driver?.Frame ?? log.Frames.LastOrDefault()?.Frame;
    }

    public static string Describe(string? reason) => reason switch
    {
        VerifyReason.PreconditionNeverMet => "the move it needed to start from never happened",
        VerifyReason.NoContact => "the required hit never connected",
        VerifyReason.TransitionNotObserved => "the next move never started",
        VerifyReason.WrongState => "the character went into a different move",
        VerifyReason.ComboDropped => "the opponent recovered mid-combo",
        VerifyReason.DriverTimeout => "the scripted inputs timed out",
        VerifyReason.InputInjectionUnavailable => "this engine build cannot receive scripted input",
        VerifyReason.TelemetryMissing => "the engine did not report what was needed",
        VerifyReason.NoMatchFrames => "no match was recorded",
        VerifyReason.TraceIntegrity => "the recording was incomplete or out of order",
        VerifyReason.PlanMismatch => "the recording does not belong to this route",
        VerifyReason.RoundChanged => "the round ended during the test",
        VerifyReason.PlayerDefeated => "a fighter was knocked out during the test",
        null => "no reason recorded",
        var other => other
    };

    private static IReadOnlyList<string> Hints(string? reason) => reason switch
    {
        VerifyReason.PreconditionNeverMet =>
            ["The previous step probably did not lead here. Look at that transition's gate for conditions the plan could not satisfy.",
             "Open the failing step in the Triggers lens: unmodelled conditions are listed on the edge."],
        VerifyReason.NoContact =>
            ["The move may have whiffed at this spacing, or its hit needs a different timing. Try a longer approach or another route.",
             "A cancel that requires 'move hit' cannot fire if the earlier hit never connected."],
        VerifyReason.TransitionNotObserved =>
            ["The inputs were fed but the engine never entered the next state. A trigger the static graph could not model may be blocking it.",
             "Open the edge's controller in the Triggers lens and check its unmodelled conditions."],
        VerifyReason.WrongState =>
            ["Another controller took priority and sent the character elsewhere. Compare the other states reachable from the source in the State Graph."],
        VerifyReason.ComboDropped =>
            ["The opponent left hitstun before the last move. Prefer a route with faster cancels or an on-hit link; check the move's hit pause and ground type."],
        VerifyReason.InputInjectionUnavailable =>
            ["Choose the X-Ray sandbox engine build in Playback setup; the installed engine cannot be driven."],
        VerifyReason.TelemetryMissing or VerifyReason.NoMatchFrames =>
            ["Open the trace: if it is empty the engine probably failed to start or exited early."],
        VerifyReason.TraceIntegrity or VerifyReason.PlanMismatch =>
            ["Replay the combo; the previous recording is not trustworthy evidence."],
        _ => []
    };
}
