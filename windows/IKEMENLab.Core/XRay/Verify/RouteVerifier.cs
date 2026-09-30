using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Runtime;

namespace IKEMENLab.Core.XRay.Verify;

public enum VerifyStatus { Verified, Failed, Inconclusive }

public enum StepOutcome { Observed, NotObserved, NotReached }

/// <summary>Why a route failed or could not be judged. Stable codes: agents switch on them.</summary>
public static class VerifyReason
{
    public const string InputInjectionUnavailable = "InputInjectionUnavailable";
    public const string TelemetryMissing = "TelemetryMissing";
    public const string NoMatchFrames = "NoMatchFrames";
    public const string PreconditionNeverMet = "PreconditionNeverMet";
    public const string NoContact = "NoContact";
    public const string TransitionNotObserved = "TransitionNotObserved";
    public const string WrongState = "WrongState";
    public const string ComboDropped = "ComboDropped";
    public const string DriverTimeout = "DriverTimeout";
}

public sealed record StepVerdict(
    int Index, string EdgeId, string FromId, string ToId, StepOutcome Outcome, string? Reason, string? Detail,
    long? InputFrame, long? ContactFrame, long? TransitionFrame, int? ObservedAfter, IReadOnlyList<string> RuntimeRules);

public sealed record ContinuityVerdict(bool Checked, bool Continuous, long? FirstHitFrame, long? LastFrame, long? DropFrame, int? DropGapFrames, string? Detail);

public sealed record VerificationReport(
    string Character, string RouteKey, VerifyStatus Status, string? Reason, int? FailedStep, IReadOnlyList<StepVerdict> Steps,
    ContinuityVerdict Continuity, int FramesObserved, string? EngineVersion, IReadOnlyList<string> Notes)
{
    public const string SchemaVersion = "ikemenlab.xray.verify/1";

    /// <summary>The only place in the product where <see cref="Confidence.RuntimeVerified"/> is produced, and only for a fully verified route.</summary>
    public Confidence RouteConfidence => Status == VerifyStatus.Verified ? Confidence.RuntimeVerified : Confidence.Inferred;
}

/// <summary>
/// Reads a trace against the plan that produced it and decides, from engine facts alone, whether every transition occurred and P2
/// stayed in one continuous combo. "Verified" needs all of it; a missing ingredient is Inconclusive, never Verified.
/// Static edge confidence is not changed here: the verdict is reported beside it.
/// </summary>
public static class RouteVerifier
{
    /// <summary>Frames of P2 outside a hit state allowed before it counts as a drop (a single sampling flicker is not recovery).</summary>
    public const int DropToleranceFrames = 0;

    public static VerificationReport Verify(InputPlan plan, TraceLog log, int subject = 1, int victim = 2)
    {
        var notes = new List<string>();
        var frames = log.Frames.ToList();
        var engine = log.Meta?.EngineVersion;
        VerificationReport Result(VerifyStatus status, string? reason, int? failed, IReadOnlyList<StepVerdict> steps, ContinuityVerdict c, string? note = null)
        {
            if (note is not null) notes.Add(note);
            return new VerificationReport(plan.Character, plan.RouteKey, status, reason, failed, steps, c, frames.Count, engine, notes);
        }

        var none = new ContinuityVerdict(false, false, null, null, null, null, null);
        var pending = plan.Steps.Select(s => new StepVerdict(s.Index, s.EdgeId, s.FromId, s.ToId, StepOutcome.NotReached, null, null, null, null, null, null, [])).ToList();

        var driver = log.Events.OfType<DriverEvent>().ToList();
        if (driver.Any(d => d.Kind == "inject_unavailable"))
            return Result(VerifyStatus.Inconclusive, VerifyReason.InputInjectionUnavailable, null, pending, none,
                driver.First(d => d.Kind == "inject_unavailable").Detail ?? "The engine adapter could not inject input, so the route was never played.");
        if (frames.Count == 0) return Result(VerifyStatus.Inconclusive, VerifyReason.NoMatchFrames, null, pending, none, "The trace has no match frames.");
        if (!frames.Any(f => Of(f, subject).State is not null))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TelemetryMissing, null, pending, none, "P1 StateNo was never readable, so no transition can be judged.");
        if (!frames.Any(f => Of(f, victim).State is not null || Of(f, victim).MoveType is not null))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TelemetryMissing, null, pending, none, "P2's state/moveType was never readable, so continuity cannot be judged.");

        var inputs = log.Events.OfType<InputEvent>().Where(e => e.Player == subject && e.Keys.Count > 0).ToList();
        var playStart = driver.FirstOrDefault(d => d.Kind == "plan_start")?.Frame ?? frames[0].Frame;

        var verdicts = new List<StepVerdict>();
        var cursor = 0; // index into frames: each step searches forward only
        long? firstHit = null;
        long lastStepFrame = playStart;
        var failed = (int?)null;
        string? failReason = null;
        bool inconclusive = false;

        foreach (var step in plan.Steps)
        {
            if (failed is not null) { verdicts.Add(pending[step.Index - 1]); continue; }

            var rules = new List<string>();
            var inputFrame = inputs.Where(e => e.Step == step.Index).Select(e => (long?)e.Frame).FirstOrDefault();
            if (step.Input.Count > 0 && inputFrame is null)
            {
                // The driver never pressed anything for this step. Usually that is because its precondition never held; only when it
                // did hold and still nothing was fed is the run untestable rather than failed.
                var (why, detail) = Diagnose(frames, step, cursor, subject);
                failed = step.Index;
                if (why is null)
                {
                    inconclusive = true; failReason = VerifyReason.InputInjectionUnavailable;
                    verdicts.Add(pending[step.Index - 1] with { Reason = failReason, Detail = "The precondition held but the driver never reported feeding input for this step." });
                }
                else
                {
                    failReason = why;
                    verdicts.Add(pending[step.Index - 1] with { Outcome = StepOutcome.NotObserved, Reason = why, Detail = detail });
                }

                continue;
            }

            // Find the transition: the first frame at/after the cursor where P1 is in ToState and the previous sampled P1 state was the source.
            int? fromState = step.FromState;
            var found = -1;
            int? seenInstead = null;
            var transitionSearchFrom = Math.Max(cursor, 1);
            for (var i = transitionSearchFrom; i < frames.Count; i++)
            {
                var prev = Of(frames[i - 1], subject).State;
                var cur = Of(frames[i], subject).State;
                if (cur is null || prev is null || cur == prev) continue;
                if (inputFrame is { } inf && frames[i].Frame < inf) continue;
                if (cur == step.ToState && (fromState is null || prev == fromState)) { found = i; break; }
                if (fromState is { } fsn && prev == fsn) seenInstead = cur;
            }

            if (found < 0)
            {
                failed = step.Index;
                var (why, detail) = Diagnose(frames, step, cursor, subject);
                var reason = why ?? (seenInstead is { } other ? VerifyReason.WrongState : VerifyReason.TransitionNotObserved);
                if (why is null)
                    detail = seenInstead is { } o2
                        ? $"P1 left state {fromState} for {o2}, not {step.ToState}."
                        : $"P1 never entered state {step.ToState}" + (fromState is null ? "." : $" from {fromState}.");
                failReason = reason;
                verdicts.Add(pending[step.Index - 1] with { Outcome = StepOutcome.NotObserved, Reason = reason, Detail = detail, InputFrame = inputFrame });
                continue;
            }

            // Contact: a hit must have been seen on the source move before the transition (P2 life fell, or moveHit/moveContact was set).
            long? contactFrame = null;
            if (step.Contact is not null)
            {
                // Walk back through the frames P1 spent in the source state; the flag may also still read on the first frame of the target.
                for (var i = found; i >= 0; i--)
                {
                    var sample = Of(frames[i], subject);
                    if (i < found && sample.State != fromState) break;
                    if (Contacted(sample, step.Contact)) contactFrame = frames[i].Frame;
                }

                if (contactFrame is null)
                {
                    failed = step.Index; failReason = VerifyReason.NoContact;
                    verdicts.Add(pending[step.Index - 1] with
                    {
                        Outcome = StepOutcome.NotObserved, Reason = VerifyReason.NoContact, InputFrame = inputFrame, TransitionFrame = frames[found].Frame,
                        Detail = $"P1 entered state {step.ToState}, but the required contact ({step.Contact}) was not observed before it."
                    });
                    continue;
                }

                rules.Add("runtime.contact-observed");
            }

            rules.Insert(0, "runtime.transition-observed");
            firstHit ??= contactFrame ?? FirstHitFrame(frames, victim, playStart);
            cursor = found;
            lastStepFrame = frames[found].Frame;
            verdicts.Add(new StepVerdict(step.Index, step.EdgeId, step.FromId, step.ToId, StepOutcome.Observed, null, null, inputFrame, contactFrame,
                frames[found].Frame, null, rules));
        }

        // Continuity: from P2's first hit to the last step's transition (+ tail), P2 must never leave a hit state.
        ContinuityVerdict continuity = none;
        if (failed is null)
        {
            var endFrame = lastStepFrame + Math.Max(0, Math.Min(plan.TailFrames, 3));
            continuity = Continuity(frames, victim, firstHit, lastStepFrame, endFrame);
            if (!continuity.Continuous)
            {
                failed = plan.Steps.Count;
                failReason = VerifyReason.ComboDropped;
            }
            else
            {
                for (var i = 0; i < verdicts.Count; i++)
                    verdicts[i] = verdicts[i] with { RuntimeRules = verdicts[i].RuntimeRules.Append("runtime.opponent-continuous").ToList() };
            }
        }

        var status = failed is null ? VerifyStatus.Verified : inconclusive ? VerifyStatus.Inconclusive : VerifyStatus.Failed;
        return new VerificationReport(plan.Character, plan.RouteKey, status, failReason, failed, verdicts, continuity, frames.Count, engine, notes);
    }

    /// <summary>Why a step's transition cannot have happened, from the facts: its source state never entered, or its contact never seen. Null reason = neither explains it.</summary>
    private static (string? Reason, string? Detail) Diagnose(List<FrameEvent> frames, PlanStep step, int cursor, int subject)
    {
        if (step.FromState is { } src && !frames.Skip(cursor).Any(f => Of(f, subject).State == src))
            return (VerifyReason.PreconditionNeverMet, $"P1 never reached state {src}, the source of this step.");
        if (step.Contact is not null && !frames.Skip(cursor).Any(f => Contacted(Of(f, subject), step.Contact)))
            return (VerifyReason.NoContact, $"The source state was entered but the required contact ({step.Contact}) was never observed.");
        return (null, null);
    }

    private static PlayerSample Of(FrameEvent f, int player) => player == 1 ? f.P1 : f.P2;

    private static bool Contacted(PlayerSample p, string need) => need switch
    {
        "hit" => p.MoveHit is > 0,
        "contact" => p.MoveHit is > 0 || p.MoveContact is > 0,
        "guarded" => p.MoveContact is > 0,
        _ => false
    };

    /// <summary>P2 is in a hit state when moveType is H; if the engine does not expose moveType, the 5000–5999 hit-state range stands in.</summary>
    public static bool InHitState(PlayerSample p) =>
        p.MoveType is { } mt ? mt.Equals("H", StringComparison.OrdinalIgnoreCase) || mt.Equals("Hit", StringComparison.OrdinalIgnoreCase)
            : p.State is >= 5000 and < 6000;

    private static long? FirstHitFrame(List<FrameEvent> frames, int victim, long from)
    {
        foreach (var f in frames)
            if (f.Frame >= from && InHitState(Of(f, victim))) return f.Frame;
        return null;
    }

    private static ContinuityVerdict Continuity(List<FrameEvent> frames, int victim, long? firstHit, long lastStep, long endFrame)
    {
        var start = FirstHitFrame(frames, victim, firstHit ?? 0);
        if (start is null)
            return new ContinuityVerdict(true, false, null, null, null, null, "P2 was never observed in a hit state.");

        long? drop = null;
        var gap = 0;
        foreach (var f in frames.Where(f => f.Frame >= start && f.Frame <= endFrame))
        {
            if (!InHitState(Of(f, victim)))
            {
                drop ??= f.Frame;
                gap++;
            }
            else if (drop is not null) break;
        }

        if (drop is not null && gap > DropToleranceFrames)
            return new ContinuityVerdict(true, false, start, endFrame, drop, gap,
                $"P2 left its hit state at frame {drop} for {gap} frame(s) before the route finished (frame {lastStep}).");
        return new ContinuityVerdict(true, true, start, endFrame, null, null, null);
    }

    // ------------------------------------------------------------------ JSON

    private static readonly JsonWriterOptions Json = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ToJson(VerificationReport r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Json))
        {
            w.WriteStartObject();
            w.WriteString("schema", VerificationReport.SchemaVersion);
            w.WriteString("character", r.Character);
            w.WriteString("route", r.RouteKey);
            w.WriteString("status", r.Status.ToString());
            w.WriteString("routeConfidence", r.RouteConfidence.ToString());
            if (r.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", r.Reason);
            if (r.FailedStep is { } fs) w.WriteNumber("failedStep", fs); else w.WriteNull("failedStep");
            w.WriteNumber("framesObserved", r.FramesObserved);
            if (r.EngineVersion is null) w.WriteNull("engineVersion"); else w.WriteString("engineVersion", r.EngineVersion);
            w.WritePropertyName("steps");
            w.WriteStartArray();
            foreach (var s in r.Steps)
            {
                w.WriteStartObject();
                w.WriteNumber("index", s.Index);
                w.WriteString("edge", s.EdgeId);
                w.WriteString("from", s.FromId);
                w.WriteString("to", s.ToId);
                w.WriteString("outcome", s.Outcome.ToString());
                if (s.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", s.Reason);
                if (s.Detail is null) w.WriteNull("detail"); else w.WriteString("detail", s.Detail);
                Long(w, "inputFrame", s.InputFrame);
                Long(w, "contactFrame", s.ContactFrame);
                Long(w, "transitionFrame", s.TransitionFrame);
                w.WritePropertyName("runtimeRules");
                w.WriteStartArray();
                foreach (var rule in s.RuntimeRules) w.WriteStringValue(rule);
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WritePropertyName("continuity");
            w.WriteStartObject();
            w.WriteBoolean("checked", r.Continuity.Checked);
            w.WriteBoolean("continuous", r.Continuity.Continuous);
            Long(w, "firstHitFrame", r.Continuity.FirstHitFrame);
            Long(w, "lastFrame", r.Continuity.LastFrame);
            Long(w, "dropFrame", r.Continuity.DropFrame);
            if (r.Continuity.DropGapFrames is { } g) w.WriteNumber("dropGapFrames", g); else w.WriteNull("dropGapFrames");
            if (r.Continuity.Detail is null) w.WriteNull("detail"); else w.WriteString("detail", r.Continuity.Detail);
            w.WriteEndObject();
            w.WritePropertyName("notes");
            w.WriteStartArray();
            foreach (var n in r.Notes) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Long(Utf8JsonWriter w, string name, long? v)
    {
        if (v is { } n) w.WriteNumber(name, n); else w.WriteNull(name);
    }
}
