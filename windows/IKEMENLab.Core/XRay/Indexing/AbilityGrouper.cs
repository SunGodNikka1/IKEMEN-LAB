using System.Globalization;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Indexing;

/// <summary>
/// Groups states into abilities. An ability starts where a ChangeState is gated by a literal <c>command</c> (or by
/// AILevel) and includes every state reachable from that entry, stopping at common states, the -1/-2/-3 hubs and at states
/// that many abilities share. Membership is StaticProven only along StaticProven edges; category labels are always Inferred.
/// </summary>
internal static class AbilityGrouper
{
    private const int MaxMembers = 400;

    private sealed class Seed
    {
        public required StateData Entry { get; init; }
        public HashSet<string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> EntryControllers { get; } = new(StringComparer.Ordinal);
        public bool ViaCommand { get; set; }
        public bool ViaAi { get; set; }
    }

    public static void Group(LinkContext ctx)
    {
        var b = ctx.B;
        var byId = ctx.States.States.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var seeds = FindSeeds(ctx, byId);
        if (seeds.Count == 0) return;

        // Pass 1: raw closures, to find states shared by many abilities (hubs).
        var raw = seeds.ToDictionary(s => s.Entry.Id, s => Closure(ctx, byId, s.Entry, hubs: null, staticOnly: false), StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var closure in raw.Values)
            foreach (var id in closure) counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
        var threshold = Math.Max(4, seeds.Count / 3);
        var hubs = counts.Where(kv => kv.Value >= threshold).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var hub in hubs)
            if (b.Find(hub) is { } hubObj)
            {
                b.Label(hubObj, "shared hub (many abilities pass through)", "hub", "ability.hub-exit");
                hubObj.Props["hub"] = "true";
            }

        foreach (var seed in seeds.OrderBy(s => s.Entry.Number))
        {
            var entryId = seed.Entry.Id;
            var all = Closure(ctx, byId, seed.Entry, hubs, staticOnly: false);
            var proven = Closure(ctx, byId, seed.Entry, hubs, staticOnly: true);
            var abilityId = $"ability:{seed.Entry.Number}";
            var name = AbilityName(ctx, seed);
            var ability = b.Add(ObjectKind.Ability, abilityId, name, seed.Entry.Block.Span(seed.Entry.FileId));
            ability.Props["entryState"] = entryId;
            ability.Props["entry"] = seed.ViaCommand && seed.ViaAi ? "command+ai" : seed.ViaCommand ? "command" : "ai";
            if (seed.Commands.Count > 0) ability.Props["commands"] = string.Join(",", seed.Commands.OrderBy(c => c, StringComparer.Ordinal));
            ability.Props["members"] = all.Count.ToString(CultureInfo.InvariantCulture);
            ability.Props["entryControllers"] = string.Join(",", seed.EntryControllers.OrderBy(c => c, StringComparer.Ordinal));
            var exits = hubs.Where(h => raw[entryId].Contains(h) && h != entryId).OrderBy(h => h, StringComparer.Ordinal).ToList();
            if (exits.Count > 0) ability.Props["exitsTo"] = string.Join(",", exits);

            foreach (var id in all.OrderBy(i => i, StringComparer.Ordinal))
            {
                var rule = proven.Contains(id) ? "ability.static-closure" : "ability.inferred-closure";
                b.RelatePartOf(id, abilityId, rule, b.Find(id)?.Source);
                if (byId.TryGetValue(id, out var member)) AttachMemberParts(ctx, member, abilityId, rule);
            }

            foreach (var cmd in seed.Commands)
                if (ctx.Commands.TryGetValue(cmd, out var cmdId))
                    b.RelatePartOf(cmdId, abilityId, "ability.static-closure", b.Find(cmdId)?.Source);
        }
    }

    // ------------------------------------------------------------------ seeds

    private static List<Seed> FindSeeds(LinkContext ctx, Dictionary<string, StateData> byId)
    {
        var seeds = new Dictionary<int, Seed>();
        foreach (var c in ctx.States.Controllers)
        {
            foreach (var rel in ctx.B.From(c.Id).Where(r => r.Kind == RelationKind.ChangesState))
            {
                if (!byId.TryGetValue(rel.To, out var target)) continue;
                var commands = c.Gate.Branches.SelectMany(br => br.Facets.Commands).Distinct().ToList();
                var reads = c.Obj.Prop("readsAILevel") == "true";
                if (commands.Count == 0 && !reads) continue;
                if (!seeds.TryGetValue(target.Number, out var seed))
                    seeds[target.Number] = seed = new Seed { Entry = target };
                foreach (var cmd in commands) seed.Commands.Add(cmd);
                seed.EntryControllers.Add(c.Id);
                if (commands.Count > 0) seed.ViaCommand = true;
                if (reads) seed.ViaAi = true;
            }
        }

        return seeds.Values.ToList();
    }

    // ------------------------------------------------------------------ closure

    /// <summary>State ids reachable from the entry. Stops at negative states, common/undefined/dynamic targets and hubs.</summary>
    private static HashSet<string> Closure(LinkContext ctx, Dictionary<string, StateData> byId, StateData entry,
        HashSet<string>? hubs, bool staticOnly)
    {
        var members = new HashSet<string>(StringComparer.Ordinal) { entry.Id };
        var queue = new Queue<StateData>();
        queue.Enqueue(entry);
        if (entry.Number < 0 || entry.IsCommon) return members;

        while (queue.Count > 0 && members.Count < MaxMembers)
        {
            var state = queue.Dequeue();
            foreach (var ctrl in state.Controllers)
            {
                foreach (var rel in Edges(ctx, ctrl))
                {
                    if (staticOnly && rel.Confidence != Confidence.StaticProven) continue;
                    if (rel.Confidence == Confidence.Unknown) continue;
                    // Helpers and victim states hang off the controller/hitdef; follow their states too.
                    foreach (var targetId in Targets(ctx, rel, staticOnly))
                    {
                        if (!byId.TryGetValue(targetId, out var target)) continue;
                        if (target.Number < 0 || target.IsCommon) continue;
                        if (hubs is not null && hubs.Contains(target.Id)) continue;
                        if (members.Add(target.Id)) queue.Enqueue(target);
                    }
                }
            }
        }

        return members;
    }

    private static IEnumerable<Relationship> Edges(LinkContext ctx, ControllerData c)
    {
        foreach (var r in ctx.B.From(c.Id))
            if (r.Kind is RelationKind.ChangesState or RelationKind.SpawnsHelper or RelationKind.SpawnsProjectile or RelationKind.SetsVictimState)
                yield return r;
        foreach (var r in ctx.B.From(c.Id + "/hitdef"))
            if (r.Kind is RelationKind.SetsVictimState or RelationKind.SetsAttackerState)
                yield return r;
    }

    private static IEnumerable<string> Targets(LinkContext ctx, Relationship rel, bool staticOnly)
    {
        if (rel.Kind is RelationKind.SpawnsHelper)
        {
            foreach (var r in ctx.B.From(rel.To).Where(r => r.Kind == RelationKind.HelperRunsState))
                if (!staticOnly || r.Confidence == Confidence.StaticProven) yield return r.To;
            yield break;
        }

        if (rel.Kind is RelationKind.SpawnsProjectile) yield break;
        yield return rel.To;
    }

    // ------------------------------------------------------------------ attachments

    private static void AttachMemberParts(LinkContext ctx, StateData member, string abilityId, string rule)
    {
        var b = ctx.B;
        foreach (var rel in b.From(member.Id).Where(r => r.Kind == RelationKind.UsesAnim))
            Part(b, rel.To, abilityId, rule);

        foreach (var ctrl in member.Controllers)
        {
            foreach (var rel in b.From(ctrl.Id))
            {
                switch (rel.Kind)
                {
                    case RelationKind.SpawnsHelper or RelationKind.SpawnsProjectile:
                        Part(b, rel.To, abilityId, rule);
                        break;
                    case RelationKind.ReadsVar or RelationKind.WritesVar or RelationKind.ResetsVar:
                        if (b.Find(rel.To)?.Prop("dynamic") != "true") Part(b, rel.To, abilityId, rule);
                        break;
                    case RelationKind.UsesAnim:
                        Part(b, rel.To, abilityId, rule);
                        break;
                    case RelationKind.DefinesHitDef:
                        Part(b, rel.To, abilityId, rule);
                        break;
                }
            }
        }
    }

    private static void Part(IndexBuilder b, string id, string abilityId, string rule)
    {
        b.RelatePartOf(id, abilityId, rule, b.Find(id)?.Source);
    }

    private static string AbilityName(LinkContext ctx, Seed seed)
    {
        // Prefer a command the CMD actually defines; an undefined one only names the ability as a last resort.
        var ordered = seed.Commands.OrderBy(c => ctx.Commands.ContainsKey(c) ? 0 : 1).ThenBy(c => c, StringComparer.Ordinal).ToList();
        foreach (var cmd in ordered)
        {
            if (ctx.Commands.TryGetValue(cmd, out var id) && ctx.B.Find(id) is { } obj)
                return obj.Prop("notation") is { Length: > 0 } n ? $"{CmdMoveReader.DisplayName(cmd)} ({n})" : CmdMoveReader.DisplayName(cmd);
            return cmd;
        }

        var comment = seed.Entry.Obj.Labels.FirstOrDefault(l => l.Category == "name")?.Text;
        return !string.IsNullOrWhiteSpace(comment) ? comment! : $"State {seed.Entry.Number}";
    }
}
