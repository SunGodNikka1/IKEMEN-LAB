using System.Globalization;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Indexing;

/// <summary>
/// Heuristic interpretation: ability categories, helper roles and variable names. Every label cites an Inferred rule, so
/// none of it can be mistaken for something the files literally state.
/// </summary>
internal static class LabelInferrer
{
    private const string AbilityCategory = LabelCategories.AbilityCategory;
    private const string HelperRole = LabelCategories.HelperRole;
    private const string VariableName = LabelCategories.VariableName;
    private const string VariableTrait = LabelCategories.VariableTrait;

    private static readonly string[] Priority = ["Super", "Throw", "Projectile", "Counter", "Special", "Normal", "Defensive", "Summon", "Mobility", "Mode"];
    private static readonly HashSet<string> MovementTypes = new(StringComparer.Ordinal) { "velset", "veladd", "velmul", "posset", "posadd" };
    private static readonly HashSet<string> DefenseTypes = new(StringComparer.Ordinal) { "nothitby", "hitby", "hitoverride" };
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "var", "varset", "varadd", "fvar", "set", "add", "setvar", "x", "v", "value", "temp", "reset", "init", "clear"
    };

    public static void Run(LinkContext ctx)
    {
        Abilities(ctx);
        Helpers(ctx);
        Variables(ctx);
    }

    // ------------------------------------------------------------------ abilities

    private static void Abilities(LinkContext ctx)
    {
        var b = ctx.B;
        var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in b.Relationships)
            if (r.Kind == RelationKind.PartOf)
                (members.TryGetValue(r.To, out var l) ? l : members[r.To] = []).Add(r.From);

        var byId = ctx.States.States.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var controllersById = ctx.States.Controllers.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var readerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in b.Relationships)
            if (r.Kind == RelationKind.ReadsVar) readerCounts[r.To] = readerCounts.TryGetValue(r.To, out var rc) ? rc + 1 : 1;

        foreach (var ability in b.Objects.Values.Where(o => o.Kind == ObjectKind.Ability).ToList())
        {
            var memberIds = members.TryGetValue(ability.Id, out var m) ? m : [];
            var controllers = memberIds.Where(byId.ContainsKey).SelectMany(id => byId[id].Controllers).ToList();
            var entryControllers = (ability.Prop("entryControllers") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);

            var hasHitDef = controllers.Any(c => c.Type is "hitdef" or "reversaldef" || b.Find(c.Id + "/hitdef") is not null);
            var hasVictim = controllers.Any(c => b.From(c.Id + "/hitdef").Any(r => r.Kind == RelationKind.SetsVictimState) ||
                                                 b.From(c.Id).Any(r => r.Kind == RelationKind.SetsVictimState));
            var hasBind = controllers.Any(c => b.From(c.Id).Any(r => r.Kind == RelationKind.Binds));
            var hasProjectile = controllers.Any(c => b.From(c.Id).Any(r => r.Kind == RelationKind.SpawnsProjectile));
            var helperIds = memberIds.Where(id => b.Find(id)?.Kind == ObjectKind.Helper).ToList();
            var helperWithHit = helperIds.Any(h => HelperHasHitDef(ctx, byId, h));
            var hasReversal = controllers.Any(c => c.Type == "reversaldef");
            var defensive = controllers.Any(c => DefenseTypes.Contains(c.Type));
            var hitOverrideState = controllers.Any(c => c.Type == "hitoverride" && c.Params.ContainsKey("stateno"));
            var movement = controllers.Any(c => MovementTypes.Contains(c.Type));

            var costsSuper = controllers.Concat(entryControllers.Where(controllersById.ContainsKey).Select(id => controllersById[id]))
                .SelectMany(c => b.From(c.Id))
                .Any(r => (r.Kind == RelationKind.GatedByPower && r.Prop("op") is ">=" or ">" && Num(r.Prop("value")) >= 1000) ||
                          (r.Kind == RelationKind.ResourceCost && Num(r.Prop("amount")) <= -1000))
                || memberIds.Where(byId.ContainsKey).Any(id => b.From(id).Any(r => r.Kind == RelationKind.ResourceCost && Num(r.Prop("amount")) <= -1000));

            var directions = 0;
            var buttons = 0;
            foreach (var cmd in (ability.Prop("commands") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!ctx.Commands.TryGetValue(cmd, out var cmdId) || b.Find(cmdId) is not { } co) continue;
                directions = Math.Max(directions, (int)Num(co.Prop("directionSteps")));
                buttons = Math.Max(buttons, (int)Num(co.Prop("buttonSteps")));
            }

            var writesWidely = memberIds.Where(id => b.Find(id)?.Kind == ObjectKind.Variable)
                .Any(v => readerCounts.TryGetValue(v, out var n) && n >= 5);

            var cats = new List<(string Name, string Rule)>();
            if (costsSuper) cats.Add(("Super", "cat.super"));
            if (hasVictim && hasBind) cats.Add(("Throw", "cat.throw"));
            else if (hasVictim) cats.Add(("Victim-state hit", "cat.victim-state"));
            if (hasProjectile || helperWithHit) cats.Add(("Projectile", "cat.projectile"));
            if (hasReversal || hitOverrideState) cats.Add(("Counter", "cat.counter"));
            if (hasHitDef && directions >= 2) cats.Add(("Special", "cat.special"));
            if (hasHitDef && directions <= 1 && buttons >= 1 && ability.Prop("commands") is not null) cats.Add(("Normal", "cat.normal"));
            if (defensive && !hasHitDef) cats.Add(("Defensive", "cat.defense"));
            if (helperIds.Count > 0 && !helperWithHit && !hasHitDef) cats.Add(("Summon", "cat.summon"));
            var entryObj = b.Find(ability.Prop("entryState") ?? string.Empty);
            var entersCommonState = entryObj is not null && (entryObj.Prop("common") == "true" || entryObj.Prop("engineCommon") == "true");
            if (!hasHitDef && (movement || entersCommonState)) cats.Add(("Mobility", "cat.mobility"));
            if (!hasHitDef && writesWidely) cats.Add(("Mode", "cat.mode"));

            foreach (var (name, rule) in cats) b.Label(ability, name, AbilityCategory, rule);
            var primary = cats.Select(c => c.Name).OrderBy(n => Array.IndexOf(Priority, n) is var i && i < 0 ? 99 : i).FirstOrDefault() ?? "Other";
            ability.Props["category"] = primary;
        }
    }

    private static bool HelperHasHitDef(LinkContext ctx, Dictionary<string, StateData> byId, string helperId)
    {
        foreach (var r in ctx.B.From(helperId).Where(r => r.Kind == RelationKind.HelperRunsState))
            if (byId.TryGetValue(r.To, out var s) && s.Controllers.Any(c => c.Type is "hitdef" or "reversaldef" or "projectile"))
                return true;
        return false;
    }

    // ------------------------------------------------------------------ helpers

    private static void Helpers(LinkContext ctx)
    {
        var b = ctx.B;
        var byId = ctx.States.States.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var helper in b.Objects.Values.Where(o => o.Kind == ObjectKind.Helper).ToList())
        {
            var states = b.From(helper.Id).Where(r => r.Kind == RelationKind.HelperRunsState && byId.ContainsKey(r.To)).Select(r => byId[r.To]).ToList();
            if (states.Any(s => s.Controllers.Any(c => c.Type is "hitdef" or "reversaldef" or "projectile")))
                b.Label(helper, "carries a HitDef (attacker/projectile-like)", HelperRole, "helper.has-hitdef");
            if (states.Any(s => s.Controllers.Any(c => c.Type is "bind" or "bindtoroot" or "bindtoparent" or "bindtotarget")))
                b.Label(helper, "follows another entity (Bind)", HelperRole, "helper.follower");
        }
    }

    // ------------------------------------------------------------------ variables

    private static void Variables(LinkContext ctx)
    {
        var b = ctx.B;
        var writers = new Dictionary<string, List<Relationship>>(StringComparer.Ordinal);
        var accesses = new Dictionary<string, List<Relationship>>(StringComparer.Ordinal);
        foreach (var r in b.Relationships)
        {
            if (r.Kind is RelationKind.WritesVar) (writers.TryGetValue(r.To, out var w) ? w : writers[r.To] = []).Add(r);
            if (r.Kind is RelationKind.WritesVar or RelationKind.ReadsVar) (accesses.TryGetValue(r.To, out var a) ? a : accesses[r.To] = []).Add(r);
        }

        var byCtrl = ctx.States.Controllers.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var v in b.Objects.Values.Where(o => o.Kind == ObjectKind.Variable && o.Prop("dynamic") != "true").ToList())
        {
            var ws = writers.TryGetValue(v.Id, out var wl) ? wl : [];
            var all = accesses.TryGetValue(v.Id, out var al) ? al : [];

            var literalValues = ws.Select(r => r.Prop("value")).ToList();
            if (ws.Count >= 2 && ws.All(r => r.Prop("value") is "0" or "1"))
                b.Label(v, "flag (only ever set to 0 or 1)", VariableTrait, "var.boolean-flag");
            if (ws.Any(r => r.Prop("step") is "1" or "-1"))
                b.Label(v, "counter/timer (incremented by 1)", VariableTrait, "var.counter");
            if (all.Any(r => r.Prop("scope") != "self"))
                b.Label(v, "shared across entities (Root/Parent/Helper/Target)", VariableTrait, "var.cross-entity");
            if (all.Any(r => byCtrl.TryGetValue(r.From, out var c) && c.Obj.Prop("readsAILevel") == "true"))
                b.Label(v, "used by AI logic", VariableTrait, "var.ai-read");

            var names = new List<string>();
            foreach (var r in ws)
            {
                if (!byCtrl.TryGetValue(r.From, out var c)) continue;
                var name = c.Obj.Prop("name");
                if (!string.IsNullOrWhiteSpace(name) && !GenericNames.Contains(name) && name.Length >= 3 && !name.All(char.IsDigit))
                    names.Add(name);
                else if (c.State.Obj.Labels.FirstOrDefault(l => l.Category == "name")?.Text is { Length: >= 3 } stateName)
                    names.Add(stateName);
            }

            if (names.Count > 0)
            {
                var best = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
                b.Label(v, $"~ \"{best}\"", VariableName, "var.name-from-writer");
            }

            _ = literalValues;
        }
    }

    private static double Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
}
