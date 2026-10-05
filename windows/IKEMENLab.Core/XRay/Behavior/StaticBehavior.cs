using System.Globalization;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using T = IKEMENLab.Core.XRay.Behavior.BehaviorTemplates;

namespace IKEMENLab.Core.XRay.Behavior;

/// <summary>
/// One AI rule (a ChangeState controller that reads AILevel) whose literal conditions and target fit a behavior template. It says the code contains a rule
/// consistent with the behavior — never that the rule fires, wins, or produced any observed episode.
/// </summary>
/// <param name="Role">For a template made of parts (Knockdown Chase: "approach" and "follow-up"), which part this rule supports; otherwise "rule".</param>
/// <param name="TargetKind">attack, super, guard, jump, move-forward, move-back, movement or other.</param>
public sealed record StaticRule(
    string Template, string Role, string ControllerId, string? OwnerStateId, string TargetStateId, string TargetKind,
    IReadOnlyList<BehaviorConditions.TriggerCondition> Conditions, Confidence Confidence, string Gate);

/// <summary>Finds the static AI rules consistent with each template (the "Possible" evidence level).</summary>
public static class StaticBehavior
{
    public static IReadOnlyList<StaticRule> Find(SemanticIndex index, CandidateGraph graph)
    {
        var supers = SuperStates(index);
        var rules = new List<StaticRule>();
        foreach (var c in index.AiEntryPoints())
        {
            if (c.Gate is null) continue;
            var targets = index.Outgoing(c.Id, RelationKind.ChangesState).Where(r => index.Get(r.To) is { Kind: ObjectKind.State }).ToList();
            if (targets.Count == 0) continue;
            var branches = c.Gate.Branches.Count > 0 ? c.Gate.Branches : [new GateBranch(0, [], EmptyFacets)];
            foreach (var b in branches)
            {
                var conds = c.Gate.TriggerAll.Concat(b.Lines).SelectMany(l => BehaviorConditions.FromTrigger(l.Expression)).ToList();
                if (b.Facets.Contact.Contains("moveguarded")) conds.Add(new(Condition.MoveBlocked, true, "moveguarded"));
                if (b.Facets.Power.Any(p => p.Op is ">=" or ">" && p.Value >= BehaviorConditions.MeterLevel)) conds.Add(new(Condition.HasMeter, true, "power"));
                var has = conds.Select(x => x.Condition).ToHashSet();
                var down = has.Contains(Condition.EnemyLying) || has.Contains(Condition.EnemyFalling);
                foreach (var r in targets)
                {
                    var kind = Classify(index, graph, r.To, supers);
                    var attack = kind is "attack" or "super";
                    void Add(string template, string role) => rules.Add(new StaticRule(template, role, c.Id, index.OwnerState(c.Id)?.Id, r.To, kind,
                        conds.GroupBy(x => x.Condition).Select(g => g.OrderByDescending(x => x.Required).First()).ToList(), r.Confidence, SemanticIndex.GateText(c.Gate)));
                    if (down && kind is "move-forward" or "movement") Add(T.KnockdownChase, "approach");
                    if ((down || has.Contains(Condition.EnemyGettingUp)) && attack) Add(T.KnockdownChase, "follow-up");
                    if (has.Contains(Condition.EnemyLying) && attack && CanHitDowned(index, r.To)) Add(T.GroundFollowUp, "rule");
                    if (has.Contains(Condition.EnemyGettingUp) && attack) Add(T.WakeUpPressure, "rule");
                    if (has.Contains(Condition.EnemyAirborne) && attack) Add(T.AntiAir, "rule");
                    if (has.Contains(Condition.EnemyAttacking) && attack) Add(T.Punish, "rule");
                    if (has.Contains(Condition.MoveBlocked) && attack) Add(T.Pressure, "rule");
                    if (has.Contains(Condition.EnemyAttacking) && kind == "guard") Add(T.Block, "rule");
                    if (kind == "move-back") Add(T.Retreat, "rule");
                    if (kind == "super") Add(T.Super, "rule");
                    if (has.Contains(Condition.EnemyHasProjectile) && kind is "jump" or "guard" or "attack" or "super" or "move-forward" or "move-back" or "movement")
                        Add(T.ProjectileResponse, "rule");
                }
            }
        }

        return rules.GroupBy(r => (r.Template, r.Role, r.ControllerId, r.TargetStateId)).Select(g => g.First()).ToList();
    }

    /// <summary>Whether the template has the static rules it needs to be Possible (Knockdown Chase needs both an approach rule and a follow-up rule).</summary>
    public static bool Supports(string template, IReadOnlyList<StaticRule> rules)
    {
        var mine = rules.Where(r => r.Template == template).ToList();
        return template == T.KnockdownChase ? mine.Any(r => r.Role == "approach") && mine.Any(r => r.Role == "follow-up") : mine.Count > 0;
    }

    /// <summary>
    /// Entry states of the abilities X-Ray categorises as Super (an inferred label). Only the entry: an ability's closure also reaches the ordinary moves a
    /// super can lead into, which must not be read as supers.
    /// </summary>
    public static IReadOnlySet<int> SuperStates(SemanticIndex index)
    {
        var set = new HashSet<int>();
        foreach (var a in index.Of(ObjectKind.Ability).Where(a => a.Labels.Any(l => l.Category == LabelCategories.AbilityCategory && l.Text == "Super")))
            if (a.Prop("entryState") is { } e && index.Get(e)?.Prop("number") is { } n && int.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
                set.Add(v);
        return set;
    }

    private static readonly GateFacets EmptyFacets = new([], [], [], [], [], [], false, [], [], false, 0);

    internal static string Classify(SemanticIndex index, CandidateGraph graph, string stateId, IReadOnlySet<int> supers)
    {
        var state = index.Get(stateId);
        var n = state?.Prop("number") is { } s && int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;
        if (n is >= 120 and < 160) return "guard";
        if (n is { } sn && supers.Contains(sn)) return "super";
        if (graph.Move(stateId) is { HitDefCount: > 0 } || string.Equals(state?.Prop("p.movetype"), "A", StringComparison.OrdinalIgnoreCase)) return "attack";
        if (n == 40) return "jump";
        if (n is 100 or 101) return "move-forward";
        if (n == 105) return "move-back";
        if (n == 20) return "movement";   // the common walk state: direction comes from input, so forward vs back is not known
        if (state?.Prop("p.velset") is { } vel && double.TryParse(vel.Split(',')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var vx) && vx != 0
            && !string.Equals(state.Prop("p.movetype"), "H", StringComparison.OrdinalIgnoreCase))
            return vx > 0 ? "move-forward" : "move-back";
        return "other";
    }

    /// <summary>A literal HitDef hitflag containing D (can hit a downed opponent) in the target state.</summary>
    private static bool CanHitDowned(SemanticIndex index, string stateId) =>
        index.ControllersOf(stateId).SelectMany(c => index.Outgoing(c.Id, RelationKind.DefinesHitDef)).Select(r => index.Get(r.To)?.Prop("p.hitflag"))
            .Any(f => f is not null && f.Contains('D', StringComparison.OrdinalIgnoreCase));
}
