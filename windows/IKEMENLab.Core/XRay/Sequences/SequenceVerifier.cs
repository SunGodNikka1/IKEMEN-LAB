using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Sequences;

/// <summary>
/// What one run of a sequence amounted to. These are deliberately separate:
/// <list type="bullet">
/// <item><see cref="TrueCombo"/>: every attack connected and the opponent was in hitstun, without control, on every sample from the first hit to the last.</item>
/// <item><see cref="ConnectedSequence"/>: every attack connected, but the opponent was out of hitstun or had control for some frames in between (a chase
/// after a knockdown that connects is a successful sequence, not a combo).</item>
/// <item><see cref="DidNotConnect"/>: a step did not happen (the move never started, the chase never got close) or an attack did not touch the opponent.</item>
/// <item><see cref="CouldNotTest"/>: the run could not show either way (no input injection, missing telemetry, a broken or foreign recording).</item>
/// </list>
/// </summary>
public enum SequenceVerdict { TrueCombo, ConnectedSequence, DidNotConnect, CouldNotTest }

public enum SequenceStepOutcome { Done, Failed, NotReached }

/// <summary>Stable reason codes for step failures that are not route-verifier reasons.</summary>
public static class SequenceReason
{
    public const string OpponentRecovered = "OpponentRecovered", OutOfReach = "OutOfReach", NeverAirborne = "NeverAirborne",
        NeverActionable = "NeverActionable", DriverDisagrees = "DriverDisagrees", NoContact = VerifyReason.NoContact;
}

public sealed record SequenceStepResult(
    int Index, string Kind, string Label, SequenceStepOutcome Outcome, string? Reason, string? Why, long? StartFrame, long? EndFrame, long? InputFrame,
    bool? Connected, long? ContactFrame, double? Damage, double? DistanceAtStart, IReadOnlyList<string> RuntimeRules)
{
    /// <summary>The route verifier's structured evidence for a move step that failed (null otherwise).</summary>
    public StepEvidence? Evidence { get; init; }
}

/// <summary>The opponent and the distance at the end of a run (or at the moment a step failed).</summary>
public sealed record SequenceEnd(long Frame, double? Distance, int? OpponentState, string OpponentPosture, bool? OpponentCanAct, bool? YouCanAct, double? YourPower);

public sealed record SequenceReport(
    string Character, string SequenceKey, SequenceVerdict Verdict, string? Reason, string Summary, int? FailedStep, IReadOnlyList<SequenceStepResult> Steps,
    int Attacks, int ConnectedAttacks, long? FirstContactFrame, long? LastContactFrame, int? OutOfHitstunFrames, int? OpponentCouldActFrames,
    double? Damage, SequenceEnd? End, bool ObservationComplete, IReadOnlyList<string> Notes)
{
    public const string SchemaVersion = "ikemenlab.xray.sequence/1";
    public string? PlanFingerprint { get; init; }
    public string? EngineSha256 { get; init; }
    public string? EngineVersion { get; init; }
    public string? EngineSource { get; init; }
    /// <summary>The route verifier's status/reason on the sequence's move steps (kept for transparency; its combo continuity is not this verdict).</summary>
    public string? RouteVerifierStatus { get; init; }
    public string? RouteVerifierReason { get; init; }
    public bool Succeeded => Verdict is SequenceVerdict.TrueCombo or SequenceVerdict.ConnectedSequence;
}

/// <summary>
/// Judges one run of a sequence plan. Move steps (abilities, dash) are judged by the unchanged <see cref="RouteVerifier"/>: it is given the plan's
/// move steps and the trace with the movement steps' bookkeeping removed (the recording itself is first checked against the full plan). Walk, wait,
/// chase and jump steps are judged from the driver's bookkeeping checked against the samples. Contact and the opponent's state between hits are read
/// from the samples. Only the route verifier's <c>runtime.transition-observed</c> is cited; nothing else here is a RuntimeVerified claim.
/// </summary>
public static class SequenceVerifier
{
    public static SequenceReport Verify(InputPlan plan, TraceLog log, IReadOnlyList<string>? labels = null)
    {
        var notes = new List<string>();
        var stepsOut = new List<SequenceStepResult>();
        string Label(PlanStep s) => labels is not null && s.Index - 1 < labels.Count ? labels[s.Index - 1] : s.IsAction ? s.Notes.FirstOrDefault() ?? s.Action!.Kind : $"State {s.ToState}";
        SequenceReport Report(SequenceVerdict verdict, string? reason, string summary, int? failed, VerificationReport? route = null, int attacks = 0, int connected = 0,
            long? first = null, long? last = null, int? gap = null, int? couldAct = null, double? damage = null, SequenceEnd? end = null, bool complete = false)
        {
            var steps = stepsOut.Count == plan.Steps.Count ? stepsOut
                : plan.Steps.Select(s => stepsOut.FirstOrDefault(r => r.Index == s.Index) ?? NotReached(s, Label(s))).ToList();
            return new SequenceReport(plan.Character, plan.RouteKey, verdict, reason, summary, failed, steps, attacks, connected, first, last, gap, couldAct, damage, end, complete, notes)
            {
                PlanFingerprint = InputPlanner.Fingerprint(plan), EngineSha256 = log.Meta?.EngineSha256, EngineVersion = log.Meta?.EngineVersion, EngineSource = log.Meta?.EngineSource,
                RouteVerifierStatus = route?.Status.ToString(), RouteVerifierReason = route?.Reason
            };
        }

        if (plan.Steps.Count == 0 || plan.Steps[0].IsAction)
            return Report(SequenceVerdict.CouldNotTest, VerifyReason.PlanMismatch, "Could not test: this is not a sequence plan (it must start with an ability).", null);
        if (log.Meta?.PlanFingerprint != InputPlanner.Fingerprint(plan))
            return Report(SequenceVerdict.CouldNotTest, VerifyReason.PlanMismatch, "Could not test: the recording does not belong to this sequence version.", null);
        var ordered = log.Events.Where(e => e is not TraceMeta).ToList();
        if (log.Issues.Count > 0 || ordered.Zip(ordered.Skip(1)).Any(p => p.Second.Frame < p.First.Frame))
            return Report(SequenceVerdict.CouldNotTest, VerifyReason.TraceIntegrity, "Could not test: the recording is incomplete or out of order.", null);

        // ---- move steps: the unchanged route verifier, on the move steps alone
        var moves = plan.Steps.Where(s => !s.IsAction).ToList();
        var toSub = moves.Select((s, i) => (s.Index, Sub: i + 1)).ToDictionary(x => x.Index, x => x.Sub);
        var subPlan = new InputPlan(plan.Character, plan.RouteKey, moves.Select((s, i) => s with { Index = i + 1, Attack = false }).ToList(), plan.ApproachDistance,
            plan.NeutralFrames, plan.TailFrames, plan.MaxFrames, plan.Warnings);
        var route = RouteVerifier.Verify(subPlan, Project(log, plan, toSub, InputPlanner.Fingerprint(subPlan)));

        var frames = log.Frames.ToList();
        var driver = log.Events.OfType<DriverEvent>().ToList();
        var complete = MoveObservation.DriverFinished(log);
        int? failedAt = null;
        string? failReason = null, failWhy = null;
        var routeBlamed = false;
        foreach (var step in plan.Steps)
        {
            var label = Label(step);
            if (failedAt is not null) { stepsOut.Add(NotReached(step, label)); continue; }
            if (!step.IsAction)
            {
                var v = route.Steps[toSub[step.Index] - 1];
                if (v.Outcome == StepOutcome.Observed)
                {
                    stepsOut.Add(new SequenceStepResult(step.Index, step.Kind, label, SequenceStepOutcome.Done, null, null, v.TransitionFrame, null, v.InputFrame,
                        null, null, null, DistanceAt(frames, v.TransitionFrame), v.RuntimeRules.Where(r => r == AbilityVerifier.TransitionRule).ToList()));
                    continue;
                }

                if (v.Outcome == StepOutcome.NotReached)
                {
                    // Never attempted, and no earlier step failed: the run itself stopped short (no injection, a setup timeout, a knockout, a broken
                    // recording). That says nothing about the sequence.
                    stepsOut.Add(NotReached(step, label));
                    failedAt = step.Index; failReason = route.Reason ?? VerifyReason.TelemetryMissing; routeBlamed = true;
                    failWhy = $"{label} was never attempted: {PlaybackInspector.Describe(failReason)}.";
                    continue;
                }

                failedAt = step.Index; failReason = v.Reason ?? route.Reason; routeBlamed = route.Status == VerifyStatus.Inconclusive;
                failWhy = $"{label} {(routeBlamed ? "could not be judged" : "did not happen")}: {PlaybackInspector.Describe(failReason)}" + (v.Detail is { Length: > 0 } d ? $" ({d})" : string.Empty) + ".";
                stepsOut.Add(new SequenceStepResult(step.Index, step.Kind, label, v.Outcome == StepOutcome.NotReached ? SequenceStepOutcome.NotReached : SequenceStepOutcome.Failed,
                    failReason, failWhy, null, null, v.InputFrame, null, null, null, null, []) { Evidence = v.Evidence });
                continue;
            }

            var a = step.Action!;
            var own = driver.Where(d => d.Step == step.Index).ToList();
            var ready = own.FirstOrDefault(d => d.Kind == "step_ready");
            var done = own.FirstOrDefault(d => d.Kind == "step_done");
            if (done is not null)
            {
                var at = frames.LastOrDefault(f => f.Frame <= done.Frame);
                var disagree = a.Kind switch
                {
                    StepAction.Chase => at?.Distance is not { } dist || Math.Abs(dist) > (a.Distance ?? 0) + 0.5,
                    StepAction.Jump => !string.Equals(at?.P1.StateType, "A", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                if (disagree)
                {
                    failedAt = step.Index; failReason = SequenceReason.DriverDisagrees; routeBlamed = true;
                    failWhy = $"The driver says {label} finished at frame {done.Frame}, but the samples do not show it; the recording cannot be trusted here.";
                    stepsOut.Add(Failed(step, label, failReason, failWhy, ready?.Frame, done.Frame));
                    continue;
                }

                stepsOut.Add(new SequenceStepResult(step.Index, step.Kind, label, SequenceStepOutcome.Done, null, null, ready?.Frame, done.Frame, null, null, null, null,
                    DistanceAt(frames, ready?.Frame), []));
                continue;
            }

            var stopped = own.FirstOrDefault(d => d.Kind == "chase_stopped");
            var timedOut = own.FirstOrDefault(d => d.Kind == "step_timeout");
            var neverReady = own.FirstOrDefault(d => d.Kind == "timeout");
            if (stopped is not null)
            {
                failReason = SequenceReason.OpponentRecovered;
                failWhy = $"The opponent could act again (frame {stopped.Frame}) before you got within {a.Distance}; the chase stopped there.";
            }
            else if (timedOut is not null)
            {
                failReason = a.Kind == StepAction.Jump ? SequenceReason.NeverAirborne : SequenceReason.OutOfReach;
                failWhy = a.Kind == StepAction.Jump
                    ? $"The jump never left the ground (watched until frame {timedOut.Frame})."
                    : $"You never got within {a.Distance} of the opponent in {a.Frames} frames (closest {ClosestDistance(frames, ready?.Frame, timedOut.Frame)}).";
            }
            else if (neverReady is not null)
            {
                failReason = SequenceReason.NeverActionable;
                failWhy = $"You never got control back to start {label} (waited until frame {neverReady.Frame}).";
            }
            else
            {
                // No bookkeeping for this step at all: the run ended before it (or the driver stopped).
                stepsOut.Add(NotReached(step, label));
                failedAt = step.Index; failReason = route.Reason ?? VerifyReason.TelemetryMissing; routeBlamed = true;
                failWhy = "The recording ends before this step started.";
                continue;
            }

            failedAt = step.Index;
            stepsOut.Add(Failed(step, label, failReason, failWhy, ready?.Frame, (stopped ?? timedOut ?? neverReady)!.Frame));
        }

        var attacks = plan.Steps.Where(s => s.Attack).ToList();
        if (failedAt is not null)
        {
            var end = EndAt(frames, stepsOut.FirstOrDefault(r => r.Index == failedAt)?.EndFrame ?? frames.LastOrDefault()?.Frame);
            if (routeBlamed)
                return Report(SequenceVerdict.CouldNotTest, failReason, $"Could not test: {failWhy}", failedAt, route, attacks.Count, end: end);
            return Report(SequenceVerdict.DidNotConnect, failReason, $"Did not connect: {failWhy}", failedAt, route, attacks.Count, end: end);
        }

        // Every step happened. Did every attack connect, and did the opponent get out in between?
        if (route.Status == VerifyStatus.Inconclusive && route.Reason == VerifyReason.TraceIntegrity)
            return Report(SequenceVerdict.CouldNotTest, VerifyReason.TraceIntegrity, "Could not test: the opponent was already hit before the first move started.", null, route, attacks.Count);
        if (route.Status == VerifyStatus.Inconclusive)
        {
            complete = false;
            notes.Add($"The route verifier could not finish its later checks ({route.Reason}); what follows the last step may be incomplete.");
        }

        var connectedCount = 0;
        long? firstContact = null, lastContact = null;
        double? totalDamage = null;
        for (var k = 0; k < attacks.Count; k++)
        {
            var result = stepsOut.First(r => r.Index == attacks[k].Index);
            var from = result.StartFrame!.Value;
            var until = k + 1 < attacks.Count ? stepsOut.First(r => r.Index == attacks[k + 1].Index).StartFrame!.Value : frames[^1].Frame + 1;
            var (connected, contact, damage) = Contact(frames, from, until, complete || k + 1 < attacks.Count);
            stepsOut[stepsOut.IndexOf(result)] = result with { Connected = connected, ContactFrame = contact, Damage = damage };
            if (connected == true) { connectedCount++; firstContact ??= contact; lastContact = contact; }
            if (damage is { } dmg) totalDamage = (totalDamage ?? 0) + dmg;
            if (connected == false)
            {
                var why = $"{result.Label} did not touch the opponent" + (result.DistanceAtStart is { } d ? $"; they were {d:0.##} away when it started" : string.Empty) + ".";
                stepsOut[stepsOut.IndexOf(stepsOut.First(r => r.Index == attacks[k].Index))] = stepsOut.First(r => r.Index == attacks[k].Index) with { Reason = SequenceReason.NoContact, Why = why };
                return Report(SequenceVerdict.DidNotConnect, SequenceReason.NoContact, "Did not connect: " + why, attacks[k].Index, route, attacks.Count, connectedCount,
                    firstContact, lastContact, null, null, totalDamage, EndAt(frames, frames[^1].Frame), complete);
            }

            if (connected is null)
                return Report(SequenceVerdict.CouldNotTest, VerifyReason.TelemetryMissing, $"Could not test: whether {result.Label} touched the opponent cannot be read (the watch did not finish or contact telemetry is missing).",
                    attacks[k].Index, route, attacks.Count, connectedCount, firstContact, lastContact, null, null, totalDamage, EndAt(frames, frames[^1].Frame), complete);
        }

        var finalEnd = EndAt(frames, frames[^1].Frame);
        if (attacks.Count < 2)
            return Report(SequenceVerdict.ConnectedSequence, null, "Connected: the attack touched the opponent. There is no follow-up attack, so there is no combo to judge.",
                null, route, attacks.Count, connectedCount, firstContact, lastContact, null, null, totalDamage, finalEnd, complete);

        var between = frames.Where(f => f.Frame >= firstContact && f.Frame <= lastContact).ToList();
        if (between.Any(f => f.P2.Ctrl is null || (f.P2.MoveType is null && f.P2.State is null)))
            return Report(SequenceVerdict.CouldNotTest, VerifyReason.TelemetryMissing, "Could not test: every attack connected, but whether the opponent could act in between cannot be read.",
                null, route, attacks.Count, connectedCount, firstContact, lastContact, null, null, totalDamage, finalEnd, complete);
        var outOfHitstun = between.Count(f => !RouteVerifier.InHitState(f.P2) || f.P2.Ctrl == true);
        var couldAct = between.Count(f => f.P2.Ctrl == true);
        if (outOfHitstun == 0)
            return Report(SequenceVerdict.TrueCombo, null, $"True combo: all {attacks.Count} attacks connected and the opponent stayed in hitstun from frame {firstContact} to {lastContact}.",
                null, route, attacks.Count, connectedCount, firstContact, lastContact, 0, 0, totalDamage, finalEnd, complete);
        return Report(SequenceVerdict.ConnectedSequence, null,
            $"Connected sequence: all {attacks.Count} attacks connected, but the opponent was out of hitstun for {outOfHitstun} frame(s) between them" +
            (couldAct > 0 ? $" and could act for {couldAct} of those." : " (without control)."),
            null, route, attacks.Count, connectedCount, firstContact, lastContact, outOfHitstun, couldAct, totalDamage, finalEnd, complete);
    }

    /// <summary>The trace as the route verifier sees the move steps alone: the movement steps' inputs and driver bookkeeping removed, move steps renumbered.</summary>
    internal static TraceLog Project(TraceLog log, InputPlan plan, IReadOnlyDictionary<int, int> toSub, string subFingerprint)
    {
        var actionSteps = plan.Steps.Where(s => s.IsAction).Select(s => s.Index).ToHashSet();
        var meta = log.Meta is null ? null : log.Meta with { PlanFingerprint = subFingerprint };
        var events = new List<TraceEvent>();
        foreach (var e in log.Events)
        {
            switch (e)
            {
                case TraceMeta: if (meta is not null) events.Add(meta); break;
                case InputEvent i when i.Step is { } s:
                    if (!actionSteps.Contains(s) && toSub.TryGetValue(s, out var si)) events.Add(i with { Step = si });
                    break;
                case DriverEvent d when d.Step is { } s:
                    if (!actionSteps.Contains(s) && toSub.TryGetValue(s, out var sd)) events.Add(d with { Step = sd });
                    break;
                default: events.Add(e); break;
            }
        }

        return new TraceLog { Meta = meta, Events = events, Issues = log.Issues, LineCount = log.LineCount };
    }

    /// <summary>Contact in [from, until): P2 lost life, P2 newly went into a hit state, or P1's own moveHit rose from zero. Null when nothing was seen and the watch is incomplete.</summary>
    private static (bool? Connected, long? Frame, double? Damage) Contact(List<FrameEvent> frames, long from, long until, bool complete)
    {
        var window = frames.Where(f => f.Frame >= from && f.Frame < until).ToList();
        if (window.Count == 0) return (null, null, null);
        var before = frames.LastOrDefault(f => f.Frame < from) ?? window[0];
        long? lifeDrop = null, newHit = null, ownHit = null;
        var prev = before;
        var resetSeen = before.P1.MoveHit is 0;
        foreach (var f in window)
        {
            if (lifeDrop is null && f.P2.Life is { } l && prev.P2.Life is { } pl && l < pl) lifeDrop = f.Frame;
            if (newHit is null && RouteVerifier.InHitState(f.P2) && !RouteVerifier.InHitState(prev.P2)) newHit = f.Frame;
            if (f.P1.MoveHit is 0) resetSeen = true;
            else if (ownHit is null && f.P1.MoveHit > 0 && resetSeen) ownHit = f.Frame;
            prev = f;
        }

        var lowest = window.Where(f => f.P2.Life is not null).Select(f => f.P2.Life!.Value).DefaultIfEmpty(double.NaN).Min();
        double? damage = before.P2.Life is { } b && !double.IsNaN(lowest) ? Math.Max(0, b - lowest) : null;
        var contact = new[] { lifeDrop, newHit, ownHit }.Where(x => x is not null).Min();
        if (contact is not null) return (true, contact, damage);
        return complete ? (false, null, damage) : (null, null, damage);
    }

    private static double? DistanceAt(List<FrameEvent> frames, long? frame) => frame is { } f ? frames.LastOrDefault(x => x.Frame <= f)?.Distance is { } d ? Math.Abs(d) : null : null;

    private static string ClosestDistance(List<FrameEvent> frames, long? from, long until)
    {
        var window = frames.Where(f => f.Frame >= (from ?? 0) && f.Frame <= until && f.Distance is not null).Select(f => Math.Abs(f.Distance!.Value)).ToList();
        return window.Count == 0 ? "unknown" : window.Min().ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static SequenceEnd? EndAt(List<FrameEvent> frames, long? frame)
    {
        if (frame is null || frames.LastOrDefault(f => f.Frame <= frame) is not { } at) return null;
        return new SequenceEnd(at.Frame, at.Distance is { } d ? Math.Abs(d) : null, at.P2.State, Situation.Posture(at.P2), at.P2.Ctrl, at.P1.Ctrl, at.P1.Power);
    }

    private static SequenceStepResult NotReached(PlanStep s, string label) =>
        new(s.Index, s.IsAction ? s.Action!.Kind : s.Kind, label, SequenceStepOutcome.NotReached, null, null, null, null, null, null, null, null, null, []);

    private static SequenceStepResult Failed(PlanStep s, string label, string? reason, string? why, long? start, long? end) =>
        new(s.Index, s.Action!.Kind, label, SequenceStepOutcome.Failed, reason, why, start, end, null, null, null, null, null, []);

    // ------------------------------------------------------------------ JSON / text

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static string ToJson(SequenceReport r)
    {
        var body = JsonSerializer.SerializeToNode(r, JsonOptions)!.AsObject();
        var wrapped = new System.Text.Json.Nodes.JsonObject { ["schema"] = SequenceReport.SchemaVersion };
        foreach (var kv in body.ToList()) { body.Remove(kv.Key); wrapped[kv.Key] = kv.Value; }
        wrapped["evidence"] = "Each move step's start is the route verifier's step check in this run (runtime.transition-observed). Contact, damage and the opponent's state between hits are measurements of this one run; they are not claims about the character and change nothing in the static graph.";
        return wrapped.ToJsonString(JsonOptions).Replace("\r\n", "\n");
    }

    public static string Headline(SequenceReport r) => r.Summary;

    /// <summary>The technical details view: per-step frames, reasons and ids behind the plain result.</summary>
    public static string Details(SequenceReport r, InputPlan? plan)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');
        L($"{r.Verdict}" + (r.Reason is null ? string.Empty : $" / {r.Reason}") + (r.FailedStep is { } f ? $" at step {f}" : string.Empty));
        L($"sequence: {r.SequenceKey}  plan fingerprint {r.PlanFingerprint}");
        L($"engine: {r.EngineSource}  sha256 {r.EngineSha256}  version {r.EngineVersion}");
        foreach (var s in r.Steps)
        {
            var p = plan?.Steps.FirstOrDefault(x => x.Index == s.Index);
            var tech = p is null ? string.Empty : p.IsAction ? $"  [{p.Action!.Kind}{(p.Action.Frames is { } fr ? $" frames={fr}" : string.Empty)}{(p.Action.Distance is { } d ? $" distance={d}" : string.Empty)}]"
                : $"  [{p.Kind} {p.FromId} -> {p.ToId} cmd {p.Command} edge {p.EdgeId}]";
            L($"  {s.Index}. {s.Label}: {s.Outcome}" + (s.Reason is null ? string.Empty : $" ({s.Reason})") + tech);
            L($"     frames start {F(s.StartFrame)} end {F(s.EndFrame)} input {F(s.InputFrame)} distance at start {F(s.DistanceAtStart)}" +
              (s.Connected is null ? string.Empty : $"  connected {s.Connected} at {F(s.ContactFrame)} damage {F(s.Damage)}") +
              (s.RuntimeRules.Count > 0 ? "  rules " + string.Join(",", s.RuntimeRules) : string.Empty));
            if (s.Why is { Length: > 0 } w) L($"     why: {w}");
        }

        L($"attacks {r.Attacks}, connected {r.ConnectedAttacks}; contact frames {F(r.FirstContactFrame)}-{F(r.LastContactFrame)}; out of hitstun {F(r.OutOfHitstunFrames)} frame(s), could act {F(r.OpponentCouldActFrames)}; damage {F(r.Damage)}");
        if (r.End is { } e) L($"end (frame {e.Frame}): distance {F(e.Distance)}, opponent {e.OpponentPosture} (state {F(e.OpponentState)}), you can act {F(e.YouCanAct)}, meter {F(e.YourPower)}");
        L($"watch complete: {r.ObservationComplete}; route verifier on the move steps: {r.RouteVerifierStatus}" + (r.RouteVerifierReason is null ? string.Empty : $" / {r.RouteVerifierReason}"));
        foreach (var n in r.Notes) L("note: " + n);
        return sb.ToString();
    }

    private static string F(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
    private static string F(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
    private static string F(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a";
    private static string F(bool? v) => v is { } b ? (b ? "yes" : "no") : "n/a";
}
