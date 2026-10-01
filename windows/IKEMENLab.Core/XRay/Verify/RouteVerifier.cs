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
    public const string TraceIntegrity = "TraceIntegrity";
    public const string PlanMismatch = "PlanMismatch";
    public const string RoundChanged = "RoundChanged";
    public const string PlayerDefeated = "PlayerDefeated";
}

public sealed record StepVerdict(
    int Index, string EdgeId, string FromId, string ToId, StepOutcome Outcome, string? Reason, string? Detail,
    long? InputFrame, long? ContactFrame, long? TransitionFrame, int? ObservedAfter, IReadOnlyList<string> RuntimeRules)
{
    /// <summary>Structured trace-derived facts about why this step failed. Null for observed steps and for runs that never reached this step.</summary>
    public StepEvidence? Evidence { get; init; }
}

public sealed record ContinuityVerdict(bool Checked, bool Continuous, long? FirstHitFrame, long? LastFrame, long? DropFrame, int? DropGapFrames, string? Detail);

public sealed record VerificationReport(
    string Character, string RouteKey, VerifyStatus Status, string? Reason, int? FailedStep, IReadOnlyList<StepVerdict> Steps,
    ContinuityVerdict Continuity, int FramesObserved, string? EngineVersion, IReadOnlyList<string> Notes)
{
    public const string SchemaVersion = "ikemenlab.xray.verify/1";

    /// <summary>The only place in the product where <see cref="Confidence.RuntimeVerified"/> is produced, and only for a fully verified route.</summary>
    public string? PlanFingerprint { get; init; }
    public string? EngineSha256 { get; init; }
    public string? EngineExecutable { get; init; }
    public string? EngineSource { get; init; }

    public Confidence RouteConfidence => Status == VerifyStatus.Verified ? Confidence.RuntimeVerified : Confidence.Inferred;
}

/// <summary>
/// Reads a trace against the plan that produced it and decides, from raw samples and corroborated driver bookkeeping, whether every transition occurred and P2
/// stayed in one continuous combo. "Verified" needs all of it; a missing ingredient is Inconclusive, never Verified.
/// Static edge confidence is not changed here: the verdict is reported beside it.
/// </summary>
public static class RouteVerifier
{
    /// <summary>Frames of P2 outside a hit state allowed before it counts as a drop (every recovery sample is a drop).</summary>
    public const int DropToleranceFrames = 0;

    public static VerificationReport Verify(InputPlan plan, TraceLog log, int subject = 1, int victim = 2)
    {
        if (subject != 1 || victim != 2) throw new ArgumentOutOfRangeException(nameof(subject), "M3 plans drive P1 against P2 only.");
        var notes = new List<string>();
        var frames = log.Frames.ToList();
        var observedCount = frames.Count;
        var none = new ContinuityVerdict(false, false, null, null, null, null, null);
        var pending = plan.Steps.Select(s => new StepVerdict(s.Index, s.EdgeId, s.FromId, s.ToId,
            StepOutcome.NotReached, null, null, null, null, null, null, [])).ToList();
        VerificationReport Result(VerifyStatus status, string? reason, int? failed, IReadOnlyList<StepVerdict> steps,
            ContinuityVerdict continuity, string? note = null)
        {
            if (note is not null) notes.Add(note);
            return new VerificationReport(plan.Character, plan.RouteKey, status, reason, failed, steps, continuity,
                observedCount, log.Meta?.EngineVersion, notes) { PlanFingerprint = InputPlanner.Fingerprint(plan), EngineSha256 = log.Meta?.EngineSha256,
                EngineExecutable = log.Meta?.EngineExecutable, EngineSource = log.Meta?.EngineSource };
        }
        if (plan.Steps.Count == 0 || !plan.Steps.Select(s => s.Index).SequenceEqual(Enumerable.Range(1, plan.Steps.Count)))
            return Result(VerifyStatus.Inconclusive, VerifyReason.PlanMismatch, null, pending, none, "Invalid or empty plan.");
        if (log.Issues.Count > 0 || log.Events.Where(e => e is not TraceMeta).Zip(log.Events.Where(e => e is not TraceMeta).Skip(1))
            .Any(pair => pair.Second.Frame < pair.First.Frame))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TraceIntegrity, null, pending, none,
                "The trace contains dropped, malformed, duplicated or out-of-order evidence: " + string.Join("; ", log.Issues.Select(i => i.Message)));
        if (plan.Steps.Any(s => s.FromState == s.ToState))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TelemetryMissing, null, pending, none, "State numbers alone cannot prove same-state reentry.");
        var driver = log.Events.OfType<DriverEvent>().ToList();
        // A missing binding stops the shipped driver before it can attempt anything. This is independent of --adapter.
        if (driver.Any(d => d.Kind == "inject_unavailable" &&
            (!driver.Any(s => s.Kind == "plan_start") || d.Frame >= driver.First(s => s.Kind == "plan_start").Frame)) && !log.Events.OfType<InputEvent>().Any(e => e.Phase == "input" && e.Keys.Count > 0))
            return Result(VerifyStatus.Inconclusive, VerifyReason.InputInjectionUnavailable, null, pending, none,
                driver.First(d => d.Kind == "inject_unavailable").Detail);
        if (frames.Count == 0) return Result(VerifyStatus.Inconclusive, VerifyReason.NoMatchFrames, null, pending, none);
        if (log.Meta?.PlanFingerprint != InputPlanner.Fingerprint(plan))
            return Result(VerifyStatus.Inconclusive, VerifyReason.PlanMismatch, null, pending, none,
                "The trace does not identify this exact input plan. Legacy traces remain readable but cannot certify a reconstructed plan.");
        var starts = driver.Where(d => d.Kind == "plan_start").ToList();
        if (starts.Count != 1 || (starts[0].Detail is { } route && route != plan.RouteKey))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TraceIntegrity, null, pending, none, "Expected one matching plan_start.");
        var playStart = starts[0].Frame;
        // Intro/neutral preparation may legitimately lack control or initialise life. The proof interval starts with
        // the first route input sample (which precedes applying that input), not with the engine's intro.
        var attemptStart = log.Events.OfType<InputEvent>().FirstOrDefault(e => e.Player == subject && e.Step == 1 &&
            e.Phase == "input" && e.Keys.Count > 0 && e.Frame >= playStart)?.Frame;
        frames = frames.Where(f => f.Frame >= (attemptStart ?? playStart)).ToList();
        if (frames.Count == 0) return Result(VerifyStatus.Inconclusive, VerifyReason.NoMatchFrames, null, pending, none);
        if (frames.Zip(frames.Skip(1)).Any(pair => pair.Second.Frame != pair.First.Frame + 1))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TraceIntegrity, null, pending, none, "Match samples must be contiguous and unique.");
        if (frames.Any(f => f.EngineTick is not null) && (frames.Any(f => f.EngineTick is null) ||
            frames.Zip(frames.Skip(1)).Any(p => p.Second.EngineTick < p.First.EngineTick || p.Second.EngineTick > p.First.EngineTick + 1)))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TraceIntegrity, null, pending, none, "Engine tick coverage is incomplete or skips simulation ticks.");
        if (frames.All(f => f.EngineTick is null)) notes.Add("Engine tick telemetry is unavailable; continuity assumes the loop hook samples every simulation tick. This assumption requires acceptance on the pinned build.");
        if (frames.Any(f => f.P1.State is null || f.P2.State is null || f.P2.Ctrl is null || f.P1.Life is null || f.P2.Life is null || f.Round is null))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TelemetryMissing, null, pending, none, "State, victim control, life and round telemetry must cover the run.");
        if (frames.Select(f => f.Round).Distinct().Count() != 1 || frames.Zip(frames.Skip(1)).Any(p => p.Second.P1.Life > p.First.P1.Life || p.Second.P2.Life > p.First.P2.Life))
            return Result(VerifyStatus.Inconclusive, VerifyReason.RoundChanged, null, pending, none, "Round changed or life reset/healed during the run.");
        if (frames.Any(f => f.P1.Life <= 0 || f.P2.Life <= 0))
            return Result(VerifyStatus.Failed, VerifyReason.PlayerDefeated, null, pending, none, "M3 does not certify routes across death/reset.");
        if (attemptStart is not null && (InHitState(frames[0].P2) || frames[0].P2.Ctrl != true))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TraceIntegrity, null, pending, none, "The victim was not free before the tested route.");
        var setupStop = driver.FirstOrDefault(d => d.Kind == "timeout" && d.Step is null && d.Frame >= playStart && d.Frame <= frames[^1].Frame);
        if (setupStop is not null && !driver.Any(d => d.Kind == "step_wait") &&
            log.Events.OfType<EndEvent>().Any(e => e.Frame == setupStop.Frame && (e.Reason is "neutralTimeout" or "approachTimeout")) &&
            !log.Events.OfType<InputEvent>().Any(e => e.Phase == "input" && e.Keys.Count > 0))
            return Result(VerifyStatus.Failed, VerifyReason.DriverTimeout, null, pending, none, "The driver stopped before any route attempt: " + setupStop.Detail);
        var inputs = log.Events.OfType<InputEvent>().Where(e => e.Player == subject && e.Phase == "input" && e.Keys.Count > 0 && e.Frame >= playStart).ToList();
        var verdicts = new List<StepVerdict>();
        var cursor = 0;
        long lastStepFrame = playStart;
        foreach (var step in plan.Steps)
        {
            var stepInputs = inputs.Where(e => e.Step == step.Index).ToList();
            var inputFrame = stepInputs.Select(e => (long?)e.Frame).FirstOrDefault();
            // Prefer a wait timeout only when the same step has no attempt and the stop is inside this run.
            // An attempted move, a stale stop, or arbitrary detail text cannot erase stronger frame evidence.
            var wait = driver.FirstOrDefault(d => d.Kind == "step_wait" && d.Step == step.Index && d.Detail == step.EdgeId && d.Frame >= frames[cursor].Frame);
            var stop = wait is null ? null : driver.FirstOrDefault(d => d.Kind == "timeout" && d.Step == step.Index &&
                d.Frame > wait.Frame + 180 && d.Frame <= frames[^1].Frame &&
                d.Detail == "precondition never met for " + step.EdgeId &&
                !driver.Any(ready => ready.Kind == "step_ready" && ready.Step == step.Index && ready.Frame >= wait.Frame && ready.Frame <= d.Frame));
            StepVerdict Fail(string reason, string detail)
            {
                // Evidence comes from the frames themselves. When it supports a sharper statement than the generic detail it replaces the text,
                // but the reason code is never changed here.
                var evidence = StepEvidenceBuilder.Build(step, frames, cursor, stepInputs, driver, plan.ApproachDistance);
                var sharper = FailureNarrative.Describe(step, evidence, reason);
                return pending[step.Index - 1] with
                    { Outcome = StepOutcome.NotObserved, Reason = reason, Detail = sharper ?? detail, InputFrame = inputFrame, Evidence = evidence };
            }
            VerificationReport StepFailure(string reason, string detail, bool inconclusive = false)
            {
                verdicts.Add(Fail(reason, detail));
                verdicts.AddRange(pending.Skip(step.Index));
                return Result(inconclusive ? VerifyStatus.Inconclusive : VerifyStatus.Failed, reason, step.Index, verdicts, none);
            }
            if (step.Input.Count > 0 && inputFrame is null)
            {
                if (stop is not null && !frames.Where(f => f.Frame >= wait!.Frame && f.Frame <= stop.Frame).Any(f =>
                    (step.FromState is { } srcState ? f.P1.State == srcState : f.P1.Ctrl == true) &&
                    (step.Contact is null || Contacted(f.P1, step.Contact)) &&
                    f.Frame >= frames[cursor].Frame + (step.EarliestTick ?? 0)))
                    return StepFailure(VerifyReason.PreconditionNeverMet, $"The driver stopped waiting for this step without an input attempt ({stop.Detail}); never attempted.");
                if (driver.Any(d => d.Kind == "inject_unavailable" && d.Frame >= frames[cursor].Frame))
                    return StepFailure(VerifyReason.InputInjectionUnavailable, "Input injection failed before this attempt.", true);
                if (step.FromState is { } src && frames[cursor].P1.State != src)
                    return StepFailure(VerifyReason.PreconditionNeverMet, $"P1 never reached the required source occurrence {src}.");
                if (step.Contact is not null && !frames.Skip(cursor).TakeWhile(f => f.P1.State == step.FromState).Any(f => Contacted(f.P1, step.Contact)))
                    return StepFailure(VerifyReason.NoContact, "The source move whiffed; the required contact was not observed.");
                return StepFailure(VerifyReason.InputInjectionUnavailable, "No input attempt was recorded for this step.", true);
            }
            if (inputFrame is { } attemptFrame)
            {
                var active = driver.LastOrDefault(d => d.Kind == "step_wait" && d.Frame <= attemptFrame);
                if (active is not null && (active.Step != step.Index || active.Detail != step.EdgeId))
                    return StepFailure(VerifyReason.TraceIntegrity, "Input is attributed to a different active driver step.", true);
            }
            if (inputFrame is { } inf && (inf < frames[cursor].Frame || inf >= frames[^1].Frame))
                return StepFailure(VerifyReason.TelemetryMissing, "The input attempt has no subsequent sampled response.", true);
            var limit = inputFrame is { } start ? start + step.Input.Count + step.TimeoutFrames : frames[cursor].Frame + 180 + step.TimeoutFrames;
            var found = -1;
            var sourceStart = cursor;
            // Lock later steps to the state occurrence reached by the preceding step. Never search past its first exit.
            for (var i = Math.Max(cursor + 1, 1); i < frames.Count && frames[i].Frame <= limit; i++)
            {
                var prev = frames[i - 1].P1;
                var cur = frames[i].P1;
                if (cur.State == prev.State) continue;
                if (step.FromState is null)
                {
                    if (inputFrame is { } first && frames[i].Frame <= first) continue;
                    if (cur.State == step.ToState && (prev.Ctrl == true || (prev.Ctrl is null && prev.State == 0)))
                    { found = i; sourceStart = i - 1; break; }
                    // Walking/crouching transitions can precede the button in a motion command; only an attack exit is decisive.
                    if (cur.MoveType is "A" or "Attack" || (prev.Ctrl == true && cur.Ctrl == false))
                        return StepFailure(VerifyReason.WrongState, $"P1 entered {cur.State}, not {step.ToState}, after the start input.");
                    continue;
                }
                if (prev.State != step.FromState || cur.State != step.ToState)
                    return StepFailure(VerifyReason.WrongState, $"P1 left the required source occurrence {step.FromState} for {cur.State}, not {step.ToState}.");
                if (inputFrame is { } attempt && frames[i].Frame <= attempt)
                    return StepFailure(VerifyReason.WrongState, "The source occurrence ended before this step's input could take effect.");
                found = i; break;
            }
            if (found < 0)
            {
                if (driver.Any(d => d.Kind == "inject_unavailable" && d.Frame >= frames[cursor].Frame))
                    return StepFailure(VerifyReason.InputInjectionUnavailable, "The adapter failed during this attempt.", true);
                if (frames[^1].Frame < limit && !log.Events.OfType<EndEvent>().Any())
                    return StepFailure(VerifyReason.TelemetryMissing, "The trace ended before the attempt could be judged.", true);
                return StepFailure(VerifyReason.TransitionNotObserved, $"P1 did not enter {step.ToState} during this attempt.");
            }
            if (inputFrame is { } press)
            {
                var finalPress = Enumerable.Range(0, step.Input.Count).Where(i => step.Input[i].Keys.Count > 0).DefaultIfEmpty(-1).Last();
                if (finalPress < 0) return StepFailure(VerifyReason.PlanMismatch, "An input step contains no command keys.", true);
                var finalKeys = step.Input[finalPress].Keys;
                while (finalPress > 0 && step.Input[finalPress - 1].Keys.SequenceEqual(finalKeys)) finalPress--;
                if (step.FromState is not null && step.EarliestTick is { } minimum && press < frames[sourceStart].Frame + minimum)
                    return StepFailure(VerifyReason.PreconditionNeverMet, "Input preceded the observed source-state minimum tick.");
                if (frames[found].Frame <= press + finalPress)
                    return StepFailure(VerifyReason.TraceIntegrity, "The target appeared before the final command keys were fed.", true);
                // Inputs are logged after sampling and take effect on subsequent engine updates. Validate the fed prefix,
                // reconstructing held sets because unchanged keys are intentionally not emitted again.
                var changes = log.Events.OfType<InputEvent>().Where(e => e.Player == subject && e.Step == step.Index && e.Frame >= press && e.Frame < frames[found].Frame).ToList();
                for (var tick = press; tick < frames[found].Frame && tick - press < step.Input.Count; tick++)
                {
                    var actual = changes.LastOrDefault(e => e.Frame <= tick)?.Keys ?? [];
                    var expected = step.Input[(int)(tick - press)].Keys;
                    if (!actual.OrderBy(k => k).SequenceEqual(expected.OrderBy(k => k)))
                        return StepFailure(VerifyReason.TraceIntegrity, "Reported inputs disagree with the plan prefix.", true);
                }
            }
            // Driver bookkeeping is supporting evidence, never a substitute for sampled transitions.
            if (driver.Any(d => d.Step == step.Index && (d.Kind is "step_timeout" or "timeout") && d.Frame < frames[found].Frame && d.Frame >= frames[cursor].Frame))
                return StepFailure(VerifyReason.DriverTimeout, "The driver stopped this attempt before the purported transition.");
            var done = driver.FirstOrDefault(d => d.Step == step.Index && d.Kind == "step_done" && d.Frame >= frames[cursor].Frame);
            if (done is not null && (done.Frame < frames[found].Frame || frames.FirstOrDefault(f => f.Frame == done.Frame)?.P1.State != step.ToState || done.Detail != step.ToState.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                return StepFailure(VerifyReason.TraceIntegrity, "Driver and probe disagree about this step.", true);
            long? contactFrame = null;
            if (step.Contact is not null)
            {
                var source = frames.Skip(sourceStart).Take(found - sourceStart).ToList();
                if (source.Any(f => step.Contact == "hit" ? f.P1.MoveHit is null : f.P1.MoveContact is null || (step.Contact == "guarded" && f.P1.MoveHit is null)))
                    return StepFailure(VerifyReason.TelemetryMissing, "Contact telemetry is missing in this source occurrence.", true);
                var resetSeen = false;
                for (var i = sourceStart; i < found; i++)
                {
                    var sample = frames[i].P1;
                    if (!Contacted(sample, step.Contact)) resetSeen = true;
                    var freshDamage = i > 0 && frames[i].P2.Life < frames[i - 1].P2.Life;
                    if (Contacted(sample, step.Contact) && (resetSeen || freshDamage)) { contactFrame = frames[i].Frame; break; }
                }
                if (contactFrame is null)
                    return StepFailure(VerifyReason.NoContact, "No fresh required contact was supported on this source occurrence; carried flags and target-state contact do not count.");
            }
            cursor = found;
            lastStepFrame = frames[found].Frame;
            var rules = new List<string> { "runtime.transition-observed" };
            if (contactFrame is not null) rules.Add("runtime.contact-observed");
            verdicts.Add(new StepVerdict(step.Index, step.EdgeId, step.FromId, step.ToId, StepOutcome.Observed, null, null,
                inputFrame, contactFrame, lastStepFrame, null, rules));
        }
        // The tail target bounds how long continuity must be observed to claim a Verified route, but an incomplete
        // tail is missing evidence, not proof of success. A drop we can actually see outranks it: report what we saw.
        var tailTarget = lastStepFrame + Math.Max(0, Math.Min(plan.TailFrames, 3));
        var endFrame = Math.Min(tailTarget, frames[^1].Frame);
        var tailComplete = frames[^1].Frame >= tailTarget;
        if (frames.Any(f => f.Frame < verdicts[0].TransitionFrame && InHitState(f.P2)))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TraceIntegrity, null, verdicts, none, "P2 was hit before the tested first move entered.");
        var firstHit = frames.FirstOrDefault(f => f.Frame <= lastStepFrame && InHitState(f.P2))?.Frame;
        if (firstHit is null)
            return Result(VerifyStatus.Failed, VerifyReason.ComboDropped, plan.Steps.Count, verdicts,
                new ContinuityVerdict(true, false, null, endFrame, null, null, "P2 was never hit before the final transition."));
        var interval = frames.Where(f => f.Frame >= firstHit && f.Frame <= endFrame).ToList();
        var usesMoveType = frames.Any(f => f.P2.MoveType is not null);
        var drop = interval.FirstOrDefault(f => !InHitState(f.P2) || f.P2.Ctrl == true);
        if (drop is not null)
        {
            var gap = interval.SkipWhile(f => f.Frame < drop.Frame).TakeWhile(f => !InHitState(f.P2) || f.P2.Ctrl == true).Count();
            return Result(VerifyStatus.Failed, VerifyReason.ComboDropped, plan.Steps.Count, verdicts,
                new ContinuityVerdict(true, false, firstHit, endFrame, drop.Frame, gap, "P2 left hitstun or regained control; zero gap frames are tolerated."));
        }
        if (usesMoveType && interval.Any(f => f.P2.MoveType is null))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TelemetryMissing, null, verdicts, none, "Victim moveType is missing inside the continuity interval.");
        if (!tailComplete)
            return Result(VerifyStatus.Inconclusive, VerifyReason.TelemetryMissing, null, verdicts, none, "The continuity tail is incomplete.");
        if (driver.Any(d => d.Frame >= playStart && d.Frame <= frames[^1].Frame && (d.Kind is "inject_unavailable" or "driver_error" or "driver_load_failed")))
            return Result(VerifyStatus.Inconclusive, VerifyReason.InputInjectionUnavailable, null, verdicts, none, "The driver did not finish with valid input injection.");
        if (!driver.Any(d => d.Kind == "plan_complete" && d.Frame >= endFrame) ||
            !log.Events.OfType<EndEvent>().Any(e => e.Reason == "planComplete" && e.Frame >= endFrame))
            return Result(VerifyStatus.Inconclusive, VerifyReason.TelemetryMissing, null, verdicts, none, "No completed driver run was recorded.");
        for (var i = 0; i < verdicts.Count; i++) verdicts[i] = verdicts[i] with
            { RuntimeRules = verdicts[i].RuntimeRules.Append("runtime.opponent-continuous").ToList() };
        return Result(VerifyStatus.Verified, null, null, verdicts, new ContinuityVerdict(true, true, firstHit, endFrame, null, null, null));
    }

    private static bool Contacted(PlayerSample p, string need) => need switch
    {
        "hit" => p.MoveHit is > 0,
        "contact" => p.MoveHit is > 0 || p.MoveContact is > 0,
        "guarded" => p.MoveContact is > 0 && p.MoveHit is 0,
        _ => false
    };

    /// <summary>Conservative victim-state rule. Custom TargetState relationships are not expanded by M3.</summary>
    public static bool InHitState(PlayerSample p) =>
        p.MoveType is { } mt ? mt.Equals("H", StringComparison.OrdinalIgnoreCase) || mt.Equals("Hit", StringComparison.OrdinalIgnoreCase)
            : p.State is >= 5000 and < 6000;

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
            w.WriteString("planFingerprint", r.PlanFingerprint);
            w.WriteString("engineSha256", r.EngineSha256);
            w.WriteString("engineExecutable", r.EngineExecutable);
            w.WriteString("engineSource", r.EngineSource);
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
                if (s.Evidence is { } ev) WriteEvidence(w, ev);
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

    internal static void WriteEvidence(Utf8JsonWriter w, StepEvidence e)
    {
        void I(string n, int? v) { if (v is { } x) w.WriteNumber(n, x); else w.WriteNull(n); }
        void L(string n, long? v) { if (v is { } x) w.WriteNumber(n, x); else w.WriteNull(n); }
        void D(string n, double? v) { if (v is { } x) w.WriteNumber(n, x); else w.WriteNull(n); }
        void B(string n, bool? v) { if (v is { } x) w.WriteBoolean(n, x); else w.WriteNull(n); }
        void S(string n, string? v) { if (v is null) w.WriteNull(n); else w.WriteString(n, v); }
        w.WritePropertyName("evidence");
        w.WriteStartObject();
        S("failure", e.Failure);
        I("expectedState", e.ExpectedState);
        I("sourceState", e.SourceState);
        B("sourceStateObserved", e.SourceStateObserved);
        L("sourceStateFirstFrame", e.SourceStateFirstFrame);
        L("sourceStateLastFrame", e.SourceStateLastFrame);
        S("requiredContact", e.RequiredContact);
        B("requiredContactObserved", e.RequiredContactObserved);
        I("earliestTick", e.EarliestTick);
        B("timingSatisfied", e.TimingSatisfied);
        w.WriteBoolean("inputAttempted", e.InputAttempted);
        L("firstInputFrame", e.FirstInputFrame);
        w.WritePropertyName("attemptedKeys");
        w.WriteStartArray();
        foreach (var k in e.AttemptedKeys) w.WriteStringValue(k);
        w.WriteEndArray();
        I("firstMismatchState", e.FirstMismatchState);
        L("firstMismatchFrame", e.FirstMismatchFrame);
        w.WriteBoolean("expectedStateEverObserved", e.ExpectedStateEverObserved);
        L("expectedStateFirstFrame", e.ExpectedStateFirstFrame);
        S("driverClaim", e.DriverClaim);
        L("driverClaimFrame", e.DriverClaimFrame);
        w.WriteNumber("configuredApproachDistance", e.ConfiguredApproachDistance);
        D("derivedSeparationAtInput", e.SeparationAtInput);
        D("derivedSeparationMin", e.SeparationMin);
        D("derivedSeparationMax", e.SeparationMax);
        w.WritePropertyName("derivedSeparationSources");
        w.WriteStartArray();
        foreach (var x in e.SeparationSources) w.WriteStringValue(x);
        w.WriteEndArray();
        w.WriteString("executedController", e.ExecutedController);
        w.WriteEndObject();
    }

    private static void Long(Utf8JsonWriter w, string name, long? v)
    {
        if (v is { } n) w.WriteNumber(name, n); else w.WriteNull(name);
    }
}
