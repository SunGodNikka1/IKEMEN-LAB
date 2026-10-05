using System.Globalization;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using static IKEMENLab.Core.XRay.Behavior.BehaviorConditions;

namespace IKEMENLab.Core.XRay.Behavior;

/// <summary>One line of the semantic timeline of a watched run, kept as data and rendered with the names in effect (see <see cref="BehaviorText.Event"/>).</summary>
/// <param name="Kind">round, enemy-falling, enemy-lying, enemy-getting-up, enemy-jump, enemy-attack, you-approach, you-retreat, you-attack, you-block, you-hit,
/// episode-start, episode-end.</param>
public sealed record TimelineEvent(long Frame, string Kind, int? State = null, double? From = null, double? To = null, string? Outcome = null, string? Template = null, int Round = 0);

/// <summary>Plain-language text for behavior results: names first (the user's Phase-1 names), state numbers in brackets, frames only where they help.</summary>
public static class BehaviorText
{
    private static string N(double? v) => v is { } x ? x.ToString("0", CultureInfo.InvariantCulture) : "?";

    /// <summary>"Ground Punch (230)" — the user's name when there is one; X-Ray's own name otherwise; "State 230" for a state the index does not know.</summary>
    public static string State(SemanticIndex? index, int? state)
    {
        if (state is not { } n) return "?";
        var id = "state:" + n.ToString(CultureInfo.InvariantCulture);
        if (index?.Get(id) is null) return $"State {n}";
        var name = index.NameOf(id);
        return name.StartsWith("State " + n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ? name : $"{name} ({n})";
    }

    public static string CharacterName(SemanticIndex? index) => index?.Get(index.CharacterId)?.Name ?? "The fighter";

    public static string Outcome(string outcome) => outcome switch
    {
        "connected" => "Connected", "blocked" => "Blocked", "whiffed" => "Whiffed", "hit" => "Hit by it", "not-hit" => "Not hit", "n/a" => "—", _ => "Contact unknown"
    };

    public static string Step(EpisodeStep s, SemanticIndex? index) => s.Code switch
    {
        "enemy-falling" => "Enemy knocked down (falling)",
        "enemy-lying" => "Enemy lying on the ground",
        "enemy-getting-up" => $"Enemy getting up ({N(s.From)} away)",
        "enemy-jump" => $"Enemy jumps ({N(s.From)} away)",
        "enemy-attack" => "Enemy attacks" + (s.Detail is { } d ? $" — it {d}" : string.Empty),
        "enemy-projectile" => "Enemy throws a projectile",
        "approach" => $"{Motion(s.State, index, "Moves toward them")}",
        "retreat" => $"{Motion(s.State, index, "Moves away")}",
        "distance" => $"Distance {N(s.From)} → {N(s.To)}",
        "attack" => $"{State(index, s.State)}" + (s.From is { } at ? $" at {N(at)}" : string.Empty) + (s.Detail is { } dd ? $" ({dd})" : string.Empty),
        "guard" => $"Guards ({State(index, s.State)})",
        "jump" => $"Jumps ({State(index, s.State)})",
        "meter" => $"Has {N(s.From)} power",
        "connected" or "blocked" or "whiffed" or "unknown" or "hit" or "not-hit" => Outcome(s.Code) + (s.Detail is { } od ? $" ({od})" : string.Empty),
        _ => s.Code
    };

    private static string Motion(int? state, SemanticIndex? index, string fallback) => state is null ? fallback : $"{fallback}: {State(index, state)}";

    public static IReadOnlyList<string> Steps(BehaviorEpisode e, SemanticIndex? index) => e.Steps.Select(s => Step(s, index)).ToList();

    /// <summary>"Enemy knocked down (falling) → Moves toward them: Walk Forward (20) → Distance 112 → 37 → Ground Punch (230) at 37 → Connected".</summary>
    public static string Chain(BehaviorEpisode e, SemanticIndex? index) => string.Join(" → ", Steps(e, index));

    /// <summary>The easy view: what situation triggered it, what action happened, what outcome followed.</summary>
    public static (string Situation, string Action, string Outcome) Parts(BehaviorEpisode e, SemanticIndex? index)
    {
        var situation = e.Steps.Where(s => s.Kind == StepKind.Situation || s.Kind == StepKind.Meter).Select(s => Step(s, index)).ToList();
        if (situation.Count == 0)   // a movement with no triggering event: what held when it started
            situation = e.Conditions.Where(c => c is not (Condition.DistanceIncreasing or Condition.DistanceDecreasing)).Select(BehaviorConditions.Name).ToList();
        var hasOutcome = e.Steps.Any(s => s.Kind == StepKind.Outcome);
        var action = e.Steps.Where(s => s.Kind is StepKind.Approach or StepKind.Attack or StepKind.Guard or StepKind.Jump or StepKind.Retreat || (s.Kind == StepKind.Distance && hasOutcome))
            .Select(s => Step(s, index)).ToList();
        // A movement's outcome is where it ended up.
        var outcome = e.Steps.LastOrDefault(s => s.Kind == StepKind.Outcome) is { } o ? Step(o, index)
            : e.Steps.LastOrDefault(s => s.Kind == StepKind.Distance) is { } dist ? Step(dist, index)
            : Outcome(e.Outcome);
        return (situation.Count == 0 ? "—" : string.Join("; ", situation), action.Count == 0 ? "—" : string.Join(" → ", action), outcome);
    }

    public static string Headline(BehaviorRun run)
    {
        var d = run.Detection;
        var head = $"Watched {run.Seconds} s ({d.Rounds} round{(d.Rounds == 1 ? string.Empty : "s")}) {run.SetupText}";
        if (d.Frames == 0) return head + ": nothing was recorded.";
        if (d.Episodes.Count == 0) return head + ": no behavior episode recognised.";
        var by = d.Episodes.GroupBy(e => e.Template).OrderByDescending(g => g.Count()).Select(g => $"{BehaviorTemplates.Get(g.Key).Name} ×{g.Count()}");
        return head + $": {d.Episodes.Count} episode{(d.Episodes.Count == 1 ? string.Empty : "s")} — " + string.Join(", ", by);
    }

    public static string Event(TimelineEvent e, SemanticIndex? index) => e.Kind switch
    {
        "round" => $"Round {e.Round}",
        "enemy-falling" => "Enemy knocked down",
        "enemy-lying" => "Enemy lying on the ground",
        "enemy-getting-up" => "Enemy gets up",
        "enemy-jump" => "Enemy jumps",
        "enemy-attack" => "Enemy attacks" + (e.Outcome is { } o ? $" — {(o == "connected" ? "you were hit" : o == "blocked" ? "you blocked" : o == "whiffed" ? "it missed" : "contact unknown")}" : string.Empty),
        "you-approach" => $"{CharacterNameShort(index)} moves in: {State(index, e.State)} · distance {N(e.From)} → {N(e.To)}",
        "you-retreat" => $"{CharacterNameShort(index)} backs off: {State(index, e.State)} · distance {N(e.From)} → {N(e.To)}",
        "you-attack" => $"{CharacterNameShort(index)}: {State(index, e.State)}" + (e.From is { } at ? $" at {N(at)}" : string.Empty) + (e.Outcome is { } oc ? $" — {Outcome(oc)}" : string.Empty),
        "you-block" => $"{CharacterNameShort(index)} blocks",
        "you-hit" => $"{CharacterNameShort(index)} is hit",
        "episode-start" => $"▶ {BehaviorTemplates.Get(e.Template!).Name} begins",
        "episode-end" => $"■ {BehaviorTemplates.Get(e.Template!).Name} — {Outcome(e.Outcome ?? "unknown")}",
        _ => e.Kind
    };

    private static string CharacterNameShort(SemanticIndex? index) => CharacterName(index);
}

/// <summary>The semantic timeline of a watched run: what each side did, in order, with the recognised episodes marked.</summary>
public static class BehaviorTimeline
{
    public static IReadOnlyList<TimelineEvent> Build(TraceLog log, Detection detection)
    {
        var frames = BehaviorDetector.Samples(log, detection);
        var events = new List<TimelineEvent>();
        if (frames.Count == 0) return events;
        var rounds = frames.GroupBy(f => f.Round ?? 1).ToList();
        foreach (var g in rounds)
        {
            var f = g.ToList();
            if (rounds.Count > 1) events.Add(new TimelineEvent(f[0].Frame, "round", Round: g.Key));
            var t = new BehaviorDetector.Track(f);
            for (var i = 1; i < f.Count; i++)
            {
                var p2 = f[i].P2;
                var q2 = f[i - 1].P2;
                if (Falling(p2) && !Falling(q2) && !Lying(q2)) events.Add(new TimelineEvent(f[i].Frame, "enemy-falling", p2.State, Round: g.Key));
                if (Lying(p2) && !Lying(q2)) events.Add(new TimelineEvent(f[i].Frame, "enemy-lying", p2.State, Round: g.Key));
                if (!Lying(p2) && Lying(q2)) events.Add(new TimelineEvent(f[i].Frame, "enemy-getting-up", p2.State, Round: g.Key));
                if (Airborne(p2) && !Hit(p2) && !(Airborne(q2) && !Hit(q2))) events.Add(new TimelineEvent(f[i].Frame, "enemy-jump", p2.State, Round: g.Key));
                if (Guarding(f[i].P1) && !Guarding(f[i - 1].P1)) events.Add(new TimelineEvent(f[i].Frame, "you-block", f[i].P1.State, Round: g.Key));
                if (Hit(f[i].P1) && !Hit(f[i - 1].P1)) events.Add(new TimelineEvent(f[i].Frame, "you-hit", f[i].P1.State, Round: g.Key));
            }

            foreach (var a in t.P2Attacks)
                events.Add(new TimelineEvent(f[a.Start].Frame, "enemy-attack", a.State, Outcome: Shown(t.P2Attacks, a, BehaviorDetector.OutcomeOf(f, a, false).Outcome), Round: g.Key));
            foreach (var a in t.P1Attacks)
                events.Add(new TimelineEvent(f[a.Start].Frame, "you-attack", a.State, t.D(a.Start), Outcome: Shown(t.P1Attacks, a, BehaviorDetector.OutcomeOf(f, a, true).Outcome), Round: g.Key));
            foreach (var m in t.Approaches)
                events.Add(new TimelineEvent(f[m.Start].Frame, "you-approach", m.States.FirstOrDefault(), t.D(m.Start), t.D(m.End), Round: g.Key));
            foreach (var m in t.Away)
                events.Add(new TimelineEvent(f[m.Start].Frame, "you-retreat", m.States.FirstOrDefault(), t.D(m.Start), t.D(m.End), Round: g.Key));
        }

        foreach (var e in detection.Episodes)
        {
            events.Add(new TimelineEvent(e.StartFrame, "episode-start", Template: e.Template, Round: e.Round));
            events.Add(new TimelineEvent(e.EndFrame, "episode-end", Outcome: e.Outcome, Template: e.Template, Round: e.Round));
        }

        return events.OrderBy(e => e.Frame).ThenBy(e => e.Kind switch { "round" => 0, "episode-start" => 1, "episode-end" => 9, _ => 5 }).ToList();
    }

    /// <summary>
    /// An attack state that runs straight into the same side's next attack state (a move made of several states, e.g. a startup and an active part) has
    /// not missed yet: its whiff is not shown, so the timeline never says "missed" just before it says the same move was blocked or hit.
    /// </summary>
    private static string? Shown(IReadOnlyList<BehaviorDetector.Attack> attacks, BehaviorDetector.Attack a, string outcome) =>
        outcome == "whiffed" && attacks.Any(x => x.Start == a.End + 1) ? null : outcome;
}

/// <summary>A bounded answer to "why did the AI do that?" at one moment of a watched run.</summary>
/// <param name="Context">What was observed at that moment.</param>
/// <param name="Action">What the fighter did next (within 1.5 s), or null.</param>
/// <param name="Matches">Recognised episodes around that moment.</param>
/// <param name="ConsistentRules">Static AI rules whose target is what the fighter did next and whose readable conditions held then. Consistent, not causal.</param>
public sealed record WhyAnswer(
    long Frame, string Context, IReadOnlyList<Condition> ConditionsHeld, string? Action, IReadOnlyList<BehaviorEpisode> Matches,
    IReadOnlyList<StaticRule> ConsistentRules, bool CauseKnown, string CausalStatement, string Summary);

/// <summary>
/// "Why did he do that?" — bounded to what can be established. The executing AI controller is not recorded, so the answer never says a rule fired; it
/// gives the observed context, what happened next, the matching behavior pattern, and the static rules that are consistent with it.
/// </summary>
public static class BehaviorWhy
{
    public const string NoAttribution =
        "Which AI rule actually fired is not recorded — X-Ray has no controller attribution yet. This is what was observed and which static rules are " +
        "consistent with it; it is not proof of why the AI chose it.";

    public static WhyAnswer Explain(SemanticIndex index, CandidateGraph graph, BehaviorRun run, TraceLog log, long frame)
    {
        var frames = BehaviorDetector.Samples(log, run.Detection).ToList();
        if (frames.Count == 0) throw new InvalidOperationException("The run recorded no match samples.");
        var at = frames.FindLastIndex(f => f.Frame <= frame);
        if (at < 0) at = 0;
        var round = frames[at].Round;
        var roundFrames = frames.Where(f => f.Round == round).ToList();
        var i = roundFrames.FindIndex(f => f.Frame == frames[at].Frame);
        var f0 = roundFrames[i];
        var who = BehaviorText.CharacterName(index);
        var startLife = roundFrames.FirstOrDefault(x => x.P1.Life is not null)?.P1.Life;

        var held = Enum.GetValues<Condition>().Where(c => Holds(c, roundFrames, i, startLife) == true).ToList();
        var d = Distance(f0);
        var context = $"At frame {f0.Frame} the opponent was {Situation.Posture(f0.P2)}" + (d is { } dd ? $", {dd:0} away" : string.Empty) +
                      $"; {who} was in {BehaviorText.State(index, f0.P1.State)}" + (f0.P1.Ctrl == true ? " and could act" : string.Empty) +
                      (f0.P1.Power is { } pw ? $", with {pw:0} power" : string.Empty) + ".";

        // What happened next (1.5 s): a movement toward or away (under way at that moment, or starting within 1 s) and the attack after it, an attack,
        // or the next state (e.g. a guard).
        var t = new BehaviorDetector.Track(roundFrames);
        string? action = null;
        var entered = new List<(int State, int At)>();   // each state the fighter went into next, and the sample it was entered on
        var approach = t.Approaches.FirstOrDefault(m => m.End >= i && m.Start <= i + 60);
        var retreat = t.Away.FirstOrDefault(m => m.End >= i && m.Start <= i + 60);
        var toward = approach is not null && (retreat is null || approach.Start <= retreat.Start);
        var move = toward ? approach : retreat;
        var attack = t.P1Attacks.FirstOrDefault(a => a.Start >= Math.Max(i, move?.Start ?? i) && a.Start <= i + 90);
        if (move is not null)
        {
            action = $"{who} {(move.Start < i ? "was moving" : "began")} {BehaviorText.State(index, move.States.FirstOrDefault())} {(toward ? "toward" : "away from")} the opponent " +
                     $"at frame {t.Frame(move.Start)} ({t.D(move.Start):0} away → {t.D(move.End):0})";
            entered.AddRange(move.States.Select(s => (s, move.Start + 1)));
        }

        if (attack is not null)
        {
            var o = BehaviorDetector.OutcomeOf(roundFrames, attack, true);
            action = (action is null ? $"{who} attacked" : action + ", then attacked") + $" with {BehaviorText.State(index, attack.State)} at frame {t.Frame(attack.Start)}, {t.D(attack.Start):0} away — {BehaviorText.Outcome(o.Outcome).ToLowerInvariant()}";
            if (attack.State is { } s) entered.Add((s, attack.Start));
        }
        else if (action is null)
        {
            var next = Enumerable.Range(i + 1, Math.Max(0, Math.Min(90, roundFrames.Count - i - 1))).FirstOrDefault(j => roundFrames[j].P1.State != f0.P1.State, -1);
            if (next > 0)
            {
                var p = roundFrames[next].P1;
                action = $"{who} went into {BehaviorText.State(index, p.State)} at frame {roundFrames[next].Frame}" + (Guarding(p) ? " (guarding)" : string.Empty);
                if (p.State is { } s) entered.Add((s, next));
            }
            else action = $"{who} stayed in {BehaviorText.State(index, f0.P1.State)} for the next 1.5 s";
        }

        // Episodes that cover this moment (or start within 5 frames of it, for a click slightly early).
        var matches = run.Detection.Episodes.Where(e => e.Round == (round ?? e.Round) && e.StartFrame <= f0.Frame + 5 && e.EndFrame >= f0.Frame).ToList();
        // A static rule is consistent when its target is a state the fighter went into and none of its required, readable conditions was false on the
        // sample just before that state was entered (the moment such a rule would have had to hold). Consistent is all it is: nothing says it fired.
        var rules = StaticBehavior.Find(index, graph);
        var consistent = entered.SelectMany(e => rules.Where(r => r.TargetStateId == "state:" + e.State.ToString(CultureInfo.InvariantCulture))
                .Where(r => r.Conditions.Where(c => c.Required).All(c => Holds(c.Condition, roundFrames, Math.Max(0, e.At - 1), startLife) != false)))
            .DistinctBy(r => (r.ControllerId, r.TargetStateId)).Take(5).ToList();

        var summary = "Observed context: " + string.Join(", ", held.Select(c => Name(c).ToLowerInvariant()).DefaultIfEmpty("nothing in the condition vocabulary held")) +
                      (d is { } d2 ? $"; distance {d2:0}" : string.Empty) + "." + (action is null ? string.Empty : " " + action + ".") +
                      (matches.Select(m => m.Template).Distinct().ToList() is { Count: > 0 } kinds
                          ? " This matches the " + string.Join(" and ", kinds.Select(k => BehaviorTemplates.Get(k).Name)) + (kinds.Count == 1 ? " pattern." : " patterns.")
                          : " It matches none of the behavior patterns.") +
                      (consistent.Count > 0 ? " Static AI contains a rule consistent with this behavior, but the executing controller is not known." : string.Empty);
        return new WhyAnswer(f0.Frame, context, held, action, matches, consistent, false, NoAttribution, summary);
    }

    /// <summary>
    /// One static rule in plain words: where it is, what it requires, what it only weighs (a condition inside a larger expression, e.g. a weighting term),
    /// where it goes. Says nothing about whether it fired.
    /// </summary>
    public static string RuleText(SemanticIndex index, StaticRule r)
    {
        var required = r.Conditions.Where(c => c.Required).Select(c => BehaviorConditions.Name(c.Condition)).ToList();
        var weighed = r.Conditions.Where(c => !c.Required).Select(c => BehaviorConditions.Name(c.Condition)).ToList();
        var reads = (required.Count > 0 ? "when " + string.Join(" and ", required) : string.Empty) +
                    (weighed.Count > 0 ? (required.Count > 0 ? "; " : string.Empty) + "weighs " + string.Join(", ", weighed) + " inside a larger expression" : string.Empty);
        return $"{(r.OwnerStateId is { } o ? BehaviorText.State(index, BehaviorCatalog.StateNumber(o)) : "?")}, controller {r.ControllerId}: " +
               (reads.Length == 0 ? "no recognised condition" : reads) + $" → {BehaviorText.State(index, BehaviorCatalog.StateNumber(r.TargetStateId))}";
    }
}
