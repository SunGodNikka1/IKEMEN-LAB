using System.Globalization;
using System.Text.RegularExpressions;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Model;

public sealed partial class SemanticIndex
{
    private static readonly RelationKind[] StateEdgeKinds =
        [RelationKind.ChangesState, RelationKind.SetsVictimState, RelationKind.SetsAttackerState, RelationKind.HelperRunsState];

    // ------------------------------------------------------------------ structural queries

    /// <summary>The state a controller (or its HitDef) belongs to, or null.</summary>
    public SemanticObject? OwnerState(string id)
    {
        for (var o = Get(id); o is not null; o = o.ParentId is null ? null : Get(o.ParentId))
            if (o.Kind == ObjectKind.State) return o;
        return null;
    }

    public IReadOnlyList<SemanticObject> ControllersOf(string stateId) =>
        Outgoing(stateId, RelationKind.Contains).Select(r => Get(r.To)).OfType<SemanticObject>().Where(o => o.Kind == ObjectKind.Controller).ToList();

    /// <summary>ChangeState/SelfState (and on-hit attacker-state) edges leaving a state, as transitions. Milestone 2 builds combo candidates from these.</summary>
    public IReadOnlyList<Transition> TransitionsFrom(string stateId)
    {
        var list = new List<Transition>();
        foreach (var c in ControllersOf(stateId))
        {
            foreach (var r in Outgoing(c.Id, RelationKind.ChangesState))
                list.Add(new Transition(stateId, r.To, c.Id, r.Kind, r.Confidence, r.Gate, r.Id));
            foreach (var r in Outgoing(c.Id + "/hitdef", RelationKind.SetsAttackerState))
                list.Add(new Transition(stateId, r.To, c.Id, r.Kind, r.Confidence, r.Gate, r.Id));
        }

        return list;
    }

    public IReadOnlyList<Transition> TransitionsInto(string stateId) =>
        Incoming(stateId, RelationKind.ChangesState, RelationKind.SetsAttackerState)
            .Select(r =>
            {
                var owner = OwnerState(r.From);
                return owner is null ? null : new Transition(owner.Id, stateId, r.From.EndsWith("/hitdef", StringComparison.Ordinal) ? r.From[..^7] : r.From, r.Kind, r.Confidence, r.Gate, r.Id);
            })
            .OfType<Transition>()
            .ToList();

    /// <summary>Controllers that read AILevel (each is an AI decision point), in id order.</summary>
    public IReadOnlyList<SemanticObject> AiEntryPoints() =>
        Objects.Where(o => o.Kind == ObjectKind.Controller && o.Prop("readsAILevel") == "true").ToList();

    /// <summary>Abilities carrying a category label (Throw, Projectile, Counter…). Labels are Inferred.</summary>
    public IReadOnlyList<SemanticObject> AbilitiesLabelled(string categoryText) =>
        Objects.Where(o => o.Kind == ObjectKind.Ability &&
                           o.Labels.Any(l => l.Category == LabelCategories.AbilityCategory && l.Text.Equals(categoryText, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    // ------------------------------------------------------------------ variables

    [GeneratedRegex(@"^(sysfvar|sysvar|fvar|var)\s*[\(:]\s*(-?\d+)\s*\)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VarSpec();

    /// <summary>Resolves "var(20)", "fvar:3", "sysvar(1)" or a full id to a variable object id.</summary>
    public string? ResolveVariableId(string spec)
    {
        spec = spec.Trim();
        if (Get(spec)?.Kind == ObjectKind.Variable) return spec;
        var m = VarSpec().Match(spec);
        if (!m.Success) return null;
        var id = $"var:{m.Groups[1].Value.ToLowerInvariant()}:{int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)}";
        return Get(id) is null ? null : id;
    }

    /// <summary>Every read, write and reset of one variable, with the controller (or Statedef) that does it.</summary>
    public IReadOnlyList<VarUse> VarUsage(string variableId) =>
        Incoming(variableId, RelationKind.ReadsVar, RelationKind.WritesVar, RelationKind.ResetsVar)
            .Select(r => Get(r.From) is { } from ? new VarUse(r, from, OwnerState(r.From)) : null)
            .OfType<VarUse>()
            .ToList();

    // ------------------------------------------------------------------ explain

    public Explanation? Explain(string id)
    {
        var subject = Get(id);
        if (subject is null) return null;
        var sections = new List<ExplainSection>();

        switch (subject.Kind)
        {
            case ObjectKind.State: ExplainState(subject, sections); break;
            case ObjectKind.Controller: ExplainController(subject, sections); break;
            case ObjectKind.Command: ExplainCommand(subject, sections); break;
            case ObjectKind.Variable: ExplainVariable(subject, sections); break;
            case ObjectKind.Helper: ExplainHelper(subject, sections); break;
            case ObjectKind.Ability: ExplainAbility(subject, sections); break;
            case ObjectKind.Animation: ExplainAnimation(subject, sections); break;
            default: ExplainGeneric(subject, sections); break;
        }

        sections.RemoveAll(s => s.Items.Count == 0);
        return new Explanation(subject, sections);
    }

    private ExplainItem ItemOf(string id, Relationship? via = null, string? note = null)
    {
        var o = Get(id);
        return new ExplainItem(id, o?.Name ?? id, o?.Kind ?? ObjectKind.Entity, via?.Confidence, via?.Kind.ToString(),
            via?.Evidence.FirstOrDefault()?.Source ?? o?.Source, note);
    }

    private IEnumerable<ExplainItem> Items(IEnumerable<Relationship> rels, bool target, Func<Relationship, string?>? note = null) =>
        rels.Select(r => ItemOf(target ? r.To : r.From, r, note?.Invoke(r)));

    private string? GateNote(Relationship r)
    {
        if (r.Gate is not null) return GateText(r.Gate);
        var owner = r.From.EndsWith("/hitdef", StringComparison.Ordinal) ? Get(r.From[..^7]) : Get(r.From);
        return owner?.Gate is { } g ? GateText(g) : null;
    }

    private void ExplainState(SemanticObject state, List<ExplainSection> sections)
    {
        var controllers = ControllersOf(state.Id);
        var ctrlIds = controllers.Select(c => c.Id).ToList();
        var allIds = ctrlIds.Concat(ctrlIds.Select(c => c + "/hitdef")).Append(state.Id).ToList();

        var entered = Incoming(state.Id, StateEdgeKinds).ToList();
        sections.Add(new("Entered by", Items(entered, target: false, GateNote).ToList()));

        var enteringControllers = entered.Select(r => r.From.EndsWith("/hitdef", StringComparison.Ordinal) ? r.From[..^7] : r.From).Distinct().ToList();
        var commands = enteringControllers.SelectMany(c => Outgoing(c, RelationKind.ReferencesCommand)).GroupBy(r => r.To).Select(g => g.First());
        sections.Add(new("Commands that lead here", Items(commands, target: true, r => Get(r.To)?.Prop("notation")).ToList()));

        sections.Add(new("Controllers", controllers.Select(c => ItemOf(c.Id, null, $"{c.Prop("type")}  {(c.Gate is null ? string.Empty : GateText(c.Gate))}")).ToList()));
        sections.Add(new("Helpers spawned", ctrlIds.SelectMany(c => Outgoing(c, RelationKind.SpawnsHelper)).Select(r => ItemOf(r.To, r)).ToList()));
        sections.Add(new("Projectiles", ctrlIds.SelectMany(c => Outgoing(c, RelationKind.SpawnsProjectile)).Select(r => ItemOf(r.To, r)).ToList()));
        sections.Add(new("Variables", allIds.SelectMany(c => Outgoing(c, RelationKind.ReadsVar, RelationKind.WritesVar, RelationKind.ResetsVar))
            .Select(r => ItemOf(r.To, r, $"{r.Kind} ({r.Prop("scope")}) by {r.From}")).ToList()));
        sections.Add(new("Animation", allIds.SelectMany(c => Outgoing(c, RelationKind.UsesAnim)).Select(r => ItemOf(r.To, r)).ToList()));
        sections.Add(new("HitDefs", ctrlIds.SelectMany(c => Outgoing(c, RelationKind.DefinesHitDef)).Select(r => ItemOf(r.To, r, Get(r.To)?.Prop("p.attr"))).ToList()));
        sections.Add(new("Leads to", allIds.SelectMany(c => Outgoing(c, StateEdgeKinds.Take(3).ToArray()))
            .Select(r => ItemOf(r.To, r, GateNote(r))).ToList()));
        sections.Add(new("Power", allIds.SelectMany(c => Outgoing(c, RelationKind.GatedByPower, RelationKind.ResourceCost))
            .Select(r => ItemOf(r.To, r, r.Kind == RelationKind.ResourceCost ? $"amount {r.Prop("amount")}" : $"{r.Prop("op")} {r.Prop("value")}")).ToList()));
        sections.Add(new("Timeline", ctrlIds.SelectMany(c => Outgoing(c, RelationKind.ActiveAtFrame)).Select(r => ItemOf(r.To, r, r.Prop("gate"))).ToList()));
        sections.Add(new("Abilities", Items(Outgoing(state.Id, RelationKind.PartOf), target: true).ToList()));
    }

    private void ExplainController(SemanticObject ctrl, List<ExplainSection> sections)
    {
        if (ctrl.Gate is { } gate)
        {
            var lines = gate.TriggerAll.Select(l => new ExplainItem(ctrl.Id, "triggerall = " + l.Text, ObjectKind.Controller, null, "gate", l.Source, null))
                .Concat(gate.Branches.GroupBy(b => b.Number).SelectMany(g => g.First().Lines.Select(l =>
                    new ExplainItem(ctrl.Id, $"trigger{g.Key} = " + l.Text, ObjectKind.Controller, null, "gate", l.Source, null)))).ToList();
            sections.Add(new("Trigger", lines));
        }

        var all = new[] { ctrl.Id, ctrl.Id + "/hitdef" };
        sections.Add(new("Does", all.SelectMany(c => Outgoing(c)).Where(r => r.Kind != RelationKind.Contains)
            .Select(r => ItemOf(r.To, r, r.Prop("value") ?? r.Prop("gate"))).ToList()));
        if (ctrl.ParentId is not null) sections.Add(new("In state", [ItemOf(ctrl.ParentId)]));
    }

    private void ExplainCommand(SemanticObject cmd, List<ExplainSection> sections)
    {
        var users = Incoming(cmd.Id, RelationKind.ReferencesCommand).ToList();
        sections.Add(new("Used by controllers", Items(users, target: false, GateNote).ToList()));
        sections.Add(new("Leads to", users.SelectMany(u => Outgoing(u.From, RelationKind.ChangesState)).Select(r => ItemOf(r.To, r)).ToList()));
        sections.Add(new("Abilities", Items(Outgoing(cmd.Id, RelationKind.PartOf), target: true).ToList()));
    }

    private void ExplainVariable(SemanticObject v, List<ExplainSection> sections)
    {
        ExplainSection Group(string title, RelationKind kind) =>
            new(title, Incoming(v.Id, kind).Select(r => ItemOf(r.From, r, $"scope {r.Prop("scope")}{(r.Prop("scopeArg") is { } a ? "(" + a + ")" : string.Empty)}{(r.Prop("value") is { } val ? ", value " + val : string.Empty)}{(r.Prop("step") is { } st ? ", step " + st : string.Empty)}")).ToList());
        sections.Add(Group("Written by", RelationKind.WritesVar));
        sections.Add(Group("Read by", RelationKind.ReadsVar));
        sections.Add(Group("Reset by", RelationKind.ResetsVar));
        sections.Add(new("Abilities", Items(Outgoing(v.Id, RelationKind.PartOf), target: true).ToList()));
    }

    private void ExplainHelper(SemanticObject h, List<ExplainSection> sections)
    {
        sections.Add(new("Spawned by", Items(Incoming(h.Id, RelationKind.SpawnsHelper), target: false, GateNote).ToList()));
        var states = Outgoing(h.Id, RelationKind.HelperRunsState).ToList();
        sections.Add(new("Runs states", Items(states, target: true).ToList()));
        var stateIds = states.Select(s => s.To).ToList();
        var ctrls = stateIds.SelectMany(ControllersOfId).ToList();
        sections.Add(new("HitDefs", ctrls.SelectMany(c => Outgoing(c, RelationKind.DefinesHitDef)).Select(r => ItemOf(r.To, r)).ToList()));
        sections.Add(new("Variables", ctrls.SelectMany(c => Outgoing(c, RelationKind.ReadsVar, RelationKind.WritesVar))
            .Select(r => ItemOf(r.To, r, $"{r.Kind} ({r.Prop("scope")})")).ToList()));
        sections.Add(new("Abilities", Items(Outgoing(h.Id, RelationKind.PartOf), target: true).ToList()));

        IEnumerable<string> ControllersOfId(string stateId) => ControllersOf(stateId).Select(c => c.Id);
    }

    private void ExplainAbility(SemanticObject ability, List<ExplainSection> sections)
    {
        var members = Incoming(ability.Id, RelationKind.PartOf).ToList();
        ExplainSection Of(string title, ObjectKind kind) =>
            new(title, members.Where(m => Get(m.From)?.Kind == kind).Select(m => ItemOf(m.From, m)).ToList());
        sections.Add(Of("Commands", ObjectKind.Command));
        sections.Add(Of("States", ObjectKind.State));
        sections.Add(Of("Helpers", ObjectKind.Helper));
        sections.Add(Of("Projectiles", ObjectKind.Projectile));
        sections.Add(Of("Variables", ObjectKind.Variable));
        sections.Add(Of("Animations", ObjectKind.Animation));
        sections.Add(Of("HitDefs", ObjectKind.HitDef));

        var stateIds = members.Where(m => Get(m.From)?.Kind == ObjectKind.State).Select(m => m.From).ToList();
        var ctrls = stateIds.SelectMany(s => ControllersOf(s)).Select(c => c.Id).ToList();
        sections.Add(new("Victim-state paths", ctrls.SelectMany(c => Outgoing(c + "/hitdef", RelationKind.SetsVictimState).Concat(Outgoing(c, RelationKind.SetsVictimState)))
            .Select(r => ItemOf(r.To, r)).ToList()));
        sections.Add(new("Power", ctrls.Concat(stateIds).SelectMany(c => Outgoing(c, RelationKind.GatedByPower, RelationKind.ResourceCost))
            .Select(r => ItemOf(r.To, r, r.Kind == RelationKind.ResourceCost ? $"amount {r.Prop("amount")}" : $"{r.Prop("op")} {r.Prop("value")}")).ToList()));
        var entry = (ability.Prop("entryControllers") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        sections.Add(new("Entry gates", entry.Select(e => ItemOf(e, null, Get(e)?.Gate is { } g ? GateText(g) : null)).ToList()));
        sections.Add(new("AI rules", entry.Where(e => Get(e)?.Prop("readsAILevel") == "true").Select(e => ItemOf(e)).ToList()));
    }

    private void ExplainAnimation(SemanticObject anim, List<ExplainSection> sections)
    {
        sections.Add(new("Frames", Outgoing(anim.Id, RelationKind.Contains).Select(r => ItemOf(r.To, r, FrameNote(r.To))).ToList()));
        sections.Add(new("Used by", Items(Incoming(anim.Id, RelationKind.UsesAnim), target: false).ToList()));
    }

    private string? FrameNote(string frameId)
    {
        var f = Get(frameId);
        return f is null ? null : $"sprite {f.Prop("group")},{f.Prop("index")}  {f.Prop("ticks")} ticks";
    }

    private void ExplainGeneric(SemanticObject o, List<ExplainSection> sections)
    {
        sections.Add(new("Outgoing", Outgoing(o.Id).Select(r => ItemOf(r.To, r)).ToList()));
        sections.Add(new("Incoming", Incoming(o.Id).Select(r => ItemOf(r.From, r)).ToList()));
    }
}
