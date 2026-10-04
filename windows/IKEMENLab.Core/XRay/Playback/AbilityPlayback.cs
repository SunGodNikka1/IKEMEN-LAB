using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

/// <summary>
/// How Play Ability will perform an ability: the one Start edge (neutral → entry state, gated by a literal command) it presses, as a one-step candidate
/// route the existing planner, sandbox, driver and route verifier play unchanged. <see cref="Alternatives"/> lists the other scriptable entry paths;
/// <see cref="Warnings"/> says what the static graph already knows may stop the command from firing.
/// </summary>
public sealed record AbilityPath(
    string AbilityId, string EntryStateId, int EntryState, CandidateEdge Edge, ComboRoute Route, IReadOnlyList<string> Alternatives, IReadOnlyList<string> Warnings)
{
    public string? Command => Edge.Commands.Count == 0 ? null : string.Join("+", Edge.Commands);
}

/// <summary>A path, or why the ability cannot be played through a command (with whether a State Preview is still possible).</summary>
public sealed record AbilityPathResult(AbilityPath? Path, string? Refused, bool CanPreview);

/// <summary>Play Ability, statically: which command path to press for an ability, and the plan options it is played with.</summary>
public static class AbilityPlayback
{
    /// <summary>The scope a Play Ability result belongs to. Never equal to a combo route key, so one kind of result is never shown as the other.</summary>
    public const string ScopePrefix = "ability-play:";
    public static string ScopeKey(string abilityId) => ScopePrefix + abilityId;

    /// <summary>Ticks watched after the move starts: the entry state's animation length plus 90, between 120 and 480 (210 when the length is unknown).</summary>
    public static int TailFramesFor(int? animTicks) => Math.Clamp((animTicks ?? 120) + 90, 120, 480);

    public static PlanOptions PlanOptionsFor(CandidateGraph graph, ComboRoute route, int approachDistance) =>
        new() { ApproachDistance = approachDistance, TailFrames = TailFramesFor(route.Steps.Count > 0 ? graph.Move(route.Steps[^1].Move.StateId)?.AnimTicks : null) };

    /// <summary>
    /// Picks the command path for <paramref name="abilityId"/>. Deterministic: scriptable Start edges into the entry state, preferring ones the
    /// planner can press from standing with no power test, then the strongest confidence, the fewest unmodelled conditions, and the edge id.
    /// It never guesses a path through other moves: an ability entered only by AI or only from another move is refused, with the reason.
    /// </summary>
    public static AbilityPathResult Resolve(CandidateGraph graph, string abilityId, PlanOptions? options = null)
    {
        var index = graph.Index;
        var ability = index.Get(abilityId);
        if (ability is null || ability.Kind != ObjectKind.Ability) return new(null, $"{abilityId} is not an ability.", false);
        if (ability.Prop("entryState") is not { } entryId || index.Get(entryId) is not { IsStub: false } entryState)
            return new(null, "The ability has no defined entry state.", false);
        var canPreview = StatePreview.CanPreview(index, entryId, out _);
        var previewHint = canPreview ? " State Preview can force its entry state for a look; that is not proof it can be performed." : string.Empty;

        var move = graph.Move(entryId);
        if (move is null) return new(null, $"Its entry state {entryState.Name} is not a move state the candidate graph can play.{previewHint}", canPreview);
        if (move.IsNeutral) return new(null, $"Its entry state {entryState.Name} is an idle (neutral) state: there is no move to perform.{previewHint}", canPreview);

        // Only a Start edge whose gate names a command can be pressed; an AILevel-gated start with no command is the AI's own decision.
        var starts = graph.From(CandidateGraph.NeutralId).Where(e => e.To == entryId && e.Kind == EdgeKind.Start && e.Commands.Count > 0).ToList();
        if (starts.Count == 0)
        {
            var viaAi = ability.Prop("entry") == "ai";
            var fromMoves = graph.Edges.Any(e => e.To == entryId && e.From != CandidateGraph.NeutralId);
            var why = viaAi
                ? "This ability is entered only by the AI (its ChangeState reads AILevel and names no command), so there is no command to press."
                : fromMoves
                    ? "This ability is only entered from other moves (a cancel or follow-up), never from neutral; Play Ability starts from neutral and does not guess a lead-in."
                    : "No command path from neutral into its entry state was found in the candidate graph.";
            return new(null, why + previewHint, canPreview);
        }

        var planOptions = options ?? new PlanOptions();
        var scriptable = new List<(CandidateEdge Edge, ComboRoute Route)>();
        var unscriptable = new List<string>();
        foreach (var e in starts)
        {
            var route = RouteFor(graph, e, move);
            var planned = InputPlanner.Plan(graph, route, planOptions);
            if (planned.Plan is null) unscriptable.Add(planned.RefusedReason ?? e.Id);
            else scriptable.Add((e, route));
        }

        if (scriptable.Count == 0)
            return new(null, "Its command cannot be pressed literally: " + string.Join(" ", unscriptable.Distinct()) + previewHint, canPreview);

        var standing = scriptable.Where(x => !NeedsNonStanding(x.Edge)).ToList();
        if (standing.Count == 0)
            return new(null, "Its command is only accepted while crouching or airborne (statetype gate); Play Ability presses it from standing, so it would not be a fair test." + previewHint, canPreview);

        var best = standing
            .OrderBy(x => NeedsPower(x.Edge) ? 1 : 0)
            .ThenBy(x => Rank(x.Edge.Confidence))
            .ThenBy(x => x.Edge.Unmodelled.Count)
            .ThenBy(x => x.Edge.Id, StringComparer.Ordinal)
            .First();

        var warnings = new List<string>();
        if (NeedsPower(best.Edge))
            warnings.Add("Its gate also tests power (" + string.Join(", ", best.Edge.Facets.Power.Select(p => $"power {p.Op} {p.Value:0.##}")) +
                         "). The test match starts with the engine's starting power, so the command may not fire.");
        if (best.Edge.Unmodelled.Count > 0)
            warnings.Add($"Its gate has {best.Edge.Unmodelled.Count} condition(s) X-Ray does not model; the script may not satisfy them.");
        var alternatives = scriptable.Where(x => x.Edge.Id != best.Edge.Id).Select(x => x.Edge.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new(new AbilityPath(abilityId, entryId, move.Number, best.Edge, best.Route, alternatives, warnings), null, canPreview);
    }

    private static bool NeedsPower(CandidateEdge e) => e.Facets.Power.Any(p => p.Op is ">=" or ">" or "=" && p.Value > 0);

    /// <summary>A positive statetype requirement that excludes standing ("c" or "a" without "s").</summary>
    private static bool NeedsNonStanding(CandidateEdge e)
    {
        var positive = e.Facets.StateTypes.Where(t => !t.EndsWith('!')).Select(t => t.ToLowerInvariant()).ToList();
        return positive.Count > 0 && !positive.Contains("s");
    }

    private static int Rank(Confidence c) => c switch { Confidence.StaticProven => 0, Confidence.Inferred => 1, _ => 2 };

    private static ComboRoute RouteFor(CandidateGraph graph, CandidateEdge edge, MoveInfo move)
    {
        double? damage = move.HitDefCount == 0 ? null : move.Damage;
        var notes = new List<string>
        {
            "Play Ability: one move from neutral through its own command. This is not a combo; only the entry and what follows it are reported."
        };
        if (edge.Facets.Power.Count > 0) notes.Add("Its gate tests power; meter is not granted by the test match.");
        return new ComboRoute
        {
            StartState = CandidateGraph.NeutralId,
            Steps = [new ComboStep(edge, move, damage, 0)],
            DamageKnown = damage ?? 0,
            DamageComplete = move.DamageExact,
            MeterSpent = (int)Math.Round(move.PowerCost),
            EndMeter = 0,
            MinFrames = move.FirstHitTick,
            FramesComplete = move.FirstHitTick is not null,
            Confidence = edge.Confidence,
            UnmodelledCount = edge.Unmodelled.Count,
            Notes = notes
        };
    }
}

public enum AbilityStatus
{
    /// <summary>The planned command inputs were followed by P1 entering the ability's entry state from neutral (the route verifier's own step check).</summary>
    Performed,
    /// <summary>The inputs were fed and the move did not start (or a different move started).</summary>
    NotPerformed,
    /// <summary>The run could not show either way (no injection, missing telemetry, a broken trace, a setup timeout…).</summary>
    Inconclusive
}

/// <summary>
/// The result of one Play Ability run. <see cref="Status"/> comes only from <see cref="RouteVerifier"/>'s step-1 verdict on the one-step plan: Performed
/// cites exactly that verdict's <c>runtime.transition-observed</c>. The verifier's combo continuity check is not used (a single move is not a combo), and
/// no route confidence is claimed. <see cref="Observed"/> is a measurement of this run, not evidence about the character.
/// </summary>
public sealed record AbilityReport(
    string Character, string AbilityId, string EntryStateId, int EntryState, string RouteKey, string EdgeId, string? Command,
    AbilityStatus Status, string? Reason, string? Detail, long? InputFrame, long? EntryFrame, MoveObservation? Observed,
    IReadOnlyList<string> RuntimeRules, IReadOnlyList<string> Notes)
{
    public const string SchemaVersion = "ikemenlab.xray.ability/1";

    /// <summary>The route verifier's own status and reason for the one-step plan, kept for transparency (it is a combo verdict and is not the ability's).</summary>
    public string? RouteVerifierStatus { get; init; }
    public string? RouteVerifierReason { get; init; }
    public string? PlanFingerprint { get; init; }
    public string? EngineSha256 { get; init; }
    public string? EngineExecutable { get; init; }
    public string? EngineVersion { get; init; }
    public string? EngineSource { get; init; }
}

/// <summary>Reads a Play Ability run from the route verifier's report on its one-step plan, plus the trace samples after the move started.</summary>
public static class AbilityVerifier
{
    public const string TransitionRule = "runtime.transition-observed";

    /// <summary>Judges <paramref name="log"/> against <paramref name="plan"/> with the route verifier, then reads it as an ability.</summary>
    public static AbilityReport Verify(string abilityId, InputPlan plan, TraceLog log) => Judge(abilityId, plan, log, RouteVerifier.Verify(plan, log));

    /// <summary>Reads an ability result from the route verifier's own report (<paramref name="entry"/>) on <paramref name="plan"/>.</summary>
    public static AbilityReport Judge(string abilityId, InputPlan plan, TraceLog log, VerificationReport entry)
    {
        var first = plan.Steps.FirstOrDefault();
        var notes = new List<string>();
        AbilityReport Make(AbilityStatus status, string? reason, string? detail, StepVerdict? step, MoveObservation? observed, IReadOnlyList<string> rules) =>
            new(plan.Character, abilityId, first?.ToId ?? string.Empty, first?.ToState ?? 0, plan.RouteKey, first?.EdgeId ?? string.Empty, first?.Command,
                status, reason, detail, step?.InputFrame, step?.TransitionFrame, observed, rules, notes)
            {
                RouteVerifierStatus = entry.Status.ToString(), RouteVerifierReason = entry.Reason, PlanFingerprint = entry.PlanFingerprint,
                EngineSha256 = entry.EngineSha256, EngineExecutable = entry.EngineExecutable, EngineVersion = entry.EngineVersion, EngineSource = entry.EngineSource
            };

        if (first is null || plan.Steps.Count != 1 || first.FromState is not null || first.IsForce || first.Input.Count == 0)
            return Make(AbilityStatus.Inconclusive, VerifyReason.PlanMismatch, "Play Ability plays exactly one command step from neutral; this plan is not one.", null, null, []);

        var step = entry.Steps.FirstOrDefault(s => s.Index == 1);
        if (step is null || step.Outcome == StepOutcome.NotReached)
        {
            // Nothing was judged about the move itself: the run stopped (or could not be trusted) before the attempt.
            var driverSaid = log.Events.OfType<DriverEvent>().FirstOrDefault(d => d.Kind == "timeout")?.Detail;
            var detail = PlaybackInspector.Describe(entry.Reason) + (entry.Reason == VerifyReason.DriverTimeout && driverSaid is not null ? $" ({driverSaid})" : string.Empty);
            return Make(AbilityStatus.Inconclusive, entry.Reason ?? VerifyReason.TelemetryMissing, detail, step, null, []);
        }

        if (step.Outcome == StepOutcome.NotObserved)
            return Make(entry.Status == VerifyStatus.Inconclusive ? AbilityStatus.Inconclusive : AbilityStatus.NotPerformed, step.Reason ?? entry.Reason, step.Detail, step, null, []);

        // The entry was observed by the route verifier's step check. Only a broken setup (the opponent hit before the move started) withdraws it.
        if (entry.Status == VerifyStatus.Inconclusive && entry.Reason == VerifyReason.TraceIntegrity)
            return Make(AbilityStatus.Inconclusive, VerifyReason.TraceIntegrity, "The recording cannot be trusted around the move: " + PlaybackInspector.Describe(VerifyReason.TraceIntegrity) + ".", step, null, []);

        var frames = log.Frames.ToList();
        var start = frames.FindIndex(f => f.Frame == step.TransitionFrame);
        var complete = MoveObservation.DriverFinished(log);
        if (entry.Status == VerifyStatus.Inconclusive)
        {
            complete = false;
            notes.Add($"The verifier could not finish its later checks ({entry.Reason}); what is shown after the start may be incomplete.");
        }

        var observed = start >= 0 ? MoveObservation.Read(frames, start, complete) : null;
        var rules = step.RuntimeRules.Where(r => r == TransitionRule).ToList();
        return Make(AbilityStatus.Performed, null, null, step, observed, rules);
    }

    /// <summary>Failure explanation for a run that did not perform the ability (null when it did): the route verifier's step-1 inspection, reworded for one move.</summary>
    public static FailureInspection? Inspect(AbilityReport ability, VerificationReport entry, TraceLog log, Func<int?, string?>? stateName = null)
    {
        if (ability.Status == AbilityStatus.Performed) return null;
        var basis = PlaybackInspector.Inspect(entry, log, stateName: stateName);
        var headline = AbilityText.Headline(ability, null);
        if (basis is null) return new FailureInspection(headline, ability.Detail ?? headline, [], null, null, null, null, null, []);
        return basis with { Headline = headline, Why = ability.Detail ?? basis.Why };
    }

    // ------------------------------------------------------------------ JSON

    private static readonly JsonWriterOptions Json = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ToJson(AbilityReport r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Json))
        {
            w.WriteStartObject();
            w.WriteString("schema", AbilityReport.SchemaVersion);
            w.WriteString("character", r.Character);
            w.WriteString("ability", r.AbilityId);
            w.WriteString("entryState", r.EntryStateId);
            w.WriteString("route", r.RouteKey);
            w.WriteString("edge", r.EdgeId);
            W(w, "command", r.Command);
            w.WriteString("status", r.Status.ToString());
            W(w, "reason", r.Reason);
            W(w, "detail", r.Detail);
            L(w, "inputFrame", r.InputFrame);
            L(w, "entryFrame", r.EntryFrame);
            w.WritePropertyName("runtimeRules");
            w.WriteStartArray();
            foreach (var x in r.RuntimeRules) w.WriteStringValue(x);
            w.WriteEndArray();
            if (r.Observed is { } o) WriteObservation(w, o); else w.WriteNull("observed");
            W(w, "routeVerifierStatus", r.RouteVerifierStatus);
            W(w, "routeVerifierReason", r.RouteVerifierReason);
            W(w, "planFingerprint", r.PlanFingerprint);
            W(w, "engineSha256", r.EngineSha256);
            W(w, "engineExecutable", r.EngineExecutable);
            W(w, "engineVersion", r.EngineVersion);
            W(w, "engineSource", r.EngineSource);
            w.WritePropertyName("notes");
            w.WriteStartArray();
            foreach (var n in r.Notes) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteString("evidence", "Performed cites only the route verifier's step check (runtime.transition-observed) for this run. Contact, damage, reaction and recovery are measurements of this one run, not claims about the character; no route or edge confidence changes.");
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static void WriteObservation(Utf8JsonWriter w, MoveObservation o)
    {
        w.WritePropertyName("observed");
        w.WriteStartObject();
        w.WriteNumber("startFrame", o.StartFrame);
        w.WriteNumber("observedThroughFrame", o.ObservedThroughFrame);
        w.WriteNumber("observedFrames", o.ObservedFrames);
        w.WriteBoolean("observationComplete", o.ObservationComplete);
        w.WriteString("connected", o.Connected);
        w.WritePropertyName("connectedBy");
        w.WriteStartArray();
        foreach (var x in o.ConnectedBy) w.WriteStringValue(x);
        w.WriteEndArray();
        L(w, "contactFrame", o.ContactFrame);
        B(w, "ownHit", o.OwnHit);
        B(w, "ownGuarded", o.OwnGuarded);
        B(w, "opponentHit", o.OpponentHit);
        B(w, "opponentGuarded", o.OpponentGuarded);
        B(w, "opponentAirborne", o.OpponentAirborne);
        B(w, "opponentKnockedDown", o.OpponentKnockedDown);
        L(w, "reactionFrame", o.ReactionFrame);
        D(w, "opponentLifeBefore", o.OpponentLifeBefore);
        D(w, "opponentLifeLowest", o.OpponentLifeLowest);
        D(w, "damage", o.Damage);
        L(w, "controlFrame", o.ControlFrame);
        if (o.FramesUntilControl is { } c) w.WriteNumber("framesUntilControl", c); else w.WriteNull("framesUntilControl");
        B(w, "keptControl", o.KeptControl);
        if (o.Situation is { } st)
        {
            w.WritePropertyName("situation");
            w.WriteStartObject();
            w.WriteNumber("frame", st.Frame);
            w.WriteString("when", st.When);
            D(w, "distance", st.Distance);
            if (st.OpponentState is { } os) w.WriteNumber("opponentState", os); else w.WriteNull("opponentState");
            w.WriteString("opponentPosture", st.OpponentPosture);
            B(w, "opponentCanAct", st.OpponentCanAct);
            B(w, "youCanAct", st.YouCanAct);
            D(w, "yourPower", st.YourPower);
            L(w, "opponentActsAt", st.OpponentActsAt);
            if (st.FramesBeforeOpponent is { } fb) w.WriteNumber("framesBeforeOpponent", fb); else w.WriteNull("framesBeforeOpponent");
            w.WriteEndObject();
        }
        w.WritePropertyName("p1States");
        w.WriteStartArray();
        foreach (var s in o.P1States) w.WriteNumberValue(s);
        w.WriteEndArray();
        w.WriteBoolean("p1StatesTruncated", o.P1StatesTruncated);
        w.WritePropertyName("missingTelemetry");
        w.WriteStartArray();
        foreach (var m in o.Missing) w.WriteStringValue(m);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void W(Utf8JsonWriter w, string n, string? v) { if (v is null) w.WriteNull(n); else w.WriteString(n, v); }
    private static void L(Utf8JsonWriter w, string n, long? v) { if (v is { } x) w.WriteNumber(n, x); else w.WriteNull(n); }
    private static void D(Utf8JsonWriter w, string n, double? v) { if (v is { } x) w.WriteNumber(n, x); else w.WriteNull(n); }
    private static void B(Utf8JsonWriter w, string n, bool? v) { if (v is { } x) w.WriteBoolean(n, x); else w.WriteNull(n); }
}

/// <summary>Plain-language lines for Play Ability and State Preview results. <c>name</c> maps a state id to the label in effect (null = X-Ray's "State N").</summary>
public static class AbilityText
{
    public static string StateLabel(string stateId, Func<string, string?>? name)
    {
        var number = stateId.StartsWith("state:", StringComparison.Ordinal) ? stateId["state:".Length..] : stateId;
        return name?.Invoke(stateId) is { Length: > 0 } n ? $"{n} (State {number})" : $"State {number}";
    }

    public static string Headline(AbilityReport r, Func<string, string?>? name) => r.Status switch
    {
        AbilityStatus.Performed => $"Performed · {StateLabel(r.EntryStateId, name)} started via “{r.Command}”" + (r.Observed is { } o ? " · " + Short(o) : string.Empty),
        AbilityStatus.NotPerformed => "Not performed: " + PlaybackInspector.Describe(r.Reason),
        _ => "Could not be tested: " + PlaybackInspector.Describe(r.Reason)
    };

    /// <summary>One short clause: connected, reaction, damage, recovery.</summary>
    public static string Short(MoveObservation o)
    {
        var parts = new List<string>
        {
            o.Connected switch { "yes" => "connected", "blocked" => "blocked", "no" => "did not connect", _ => "contact unknown" }
        };
        if (o.OpponentKnockedDown == true) parts.Add("knocked down");
        else if (o.OpponentAirborne == true) parts.Add("launched");
        if (o.Damage is > 0 and var d) parts.Add($"{d:0.##} damage");
        parts.Add(o.KeptControl == true ? "kept control throughout" : o.FramesUntilControl is { } c ? $"back in control after {c}f" : "control not regained while watched");
        return string.Join(" · ", parts);
    }

    /// <summary>The result card: what started, what it hit, what it did, when control came back, and what the evidence is.</summary>
    public static IReadOnlyList<string> Details(MoveObservation o, Func<string, string?>? name)
    {
        string F(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "n/a";
        string YesNo(bool? b) => b is { } x ? (x ? "yes" : "no") : "unknown";
        var lines = new List<string>
        {
            "Connected: " + o.Connected switch
            {
                "yes" => "yes — " + string.Join("; ", o.ConnectedBy) + (o.ContactFrame is { } cf ? $" (first at frame {cf})" : string.Empty),
                "blocked" => "blocked",
                "no" => "no — nothing touched the opponent while watched",
                _ => "unknown" + (o.ObservationComplete ? " — the telemetry needed is missing" : " — the recording stopped before the watch ended")
            },
            $"Opponent: hit {YesNo(o.OpponentHit)} · launched {YesNo(o.OpponentAirborne)} · knocked down {YesNo(o.OpponentKnockedDown)}" +
            (o.Damage is not null ? $" · {F(o.Damage)} damage (life {F(o.OpponentLifeBefore)} → {F(o.OpponentLifeLowest)})" : " · damage unknown"),
            o.KeptControl == true
                ? "Kept control throughout: this state never took control away"
            : o.FramesUntilControl is { } c
                ? $"Back in control {c} frames after the move started (frame {o.StartFrame} → {o.ControlFrame})"
                : $"Not back in control within the {o.ObservedFrames} frames watched" + (o.Missing.Contains("p1.ctrl") ? " (control is not readable on this build)" : string.Empty),
            "States: " + string.Join(" → ", o.P1States.Select(s => StateLabel("state:" + s.ToString(CultureInfo.InvariantCulture), name))) + (o.P1StatesTruncated ? " → …" : string.Empty)
        };
        if (o.Situation is { } situation) lines.Insert(0, situation.Describe());
        if (!o.ObservationComplete) lines.Add("The watch after the move did not finish, so anything not seen above may simply not have been recorded.");
        if (o.Missing.Count > 0) lines.Add("Not readable on this build: " + string.Join(", ", o.Missing));
        return lines;
    }
}
