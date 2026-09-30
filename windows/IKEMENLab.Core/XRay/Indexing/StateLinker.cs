using System.Globalization;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Indexing;

internal sealed record AnimFrameInfo(string Id, int StartTick, int Ticks);

internal sealed class LinkContext
{
    public required IndexBuilder B { get; init; }
    public required StateIndexResult States { get; init; }
    public required Dictionary<string, string> Commands { get; init; }
    public required Dictionary<int, string> AnimIds { get; init; }
    public required Dictionary<string, List<AnimFrameInfo>> AnimFrames { get; init; }
    public required bool CommonLoaded { get; init; }
    public required string CharacterId { get; init; }
    private readonly Dictionary<ControllerData, Dictionary<string, Expr>> _cache = [];

    public Expr? Param(ControllerData c, string key)
    {
        if (!c.Params.TryGetValue(key, out var entry)) return null;
        if (!_cache.TryGetValue(c, out var map)) _cache[c] = map = [];
        if (!map.TryGetValue(key, out var e)) map[key] = e = ExprParser.Parse(entry.Value);
        return e;
    }

    public SourceRef Line(ControllerData c, string key) =>
        c.Params.TryGetValue(key, out var e) ? SourceRef.At(c.FileId, e.Line) : c.Span;
}

/// <summary>Phase 2: relationships between the objects of phase 1 (state changes, victim states, helpers, animations, power…).</summary>
internal static class StateLinker
{
    public static void Link(LinkContext ctx)
    {
        var b = ctx.B;
        var charObj = b.Find(ctx.CharacterId)!;
        foreach (var state in ctx.States.States)
        {
            if (state.Number is -1 or -2 or -3)
                b.Relate(RelationKind.EntryPoint, charObj.Id, state.Id, "entry.engine-state", state.Block.Span(state.FileId),
                    props: [new("kind", "engine")]);
            LinkStatedef(ctx, state);
        }

        foreach (var c in ctx.States.Controllers)
        {
            LinkGate(ctx, c);
            LinkController(ctx, c);
        }
    }

    // ------------------------------------------------------------------ Statedef

    private static void LinkStatedef(LinkContext ctx, StateData state)
    {
        var b = ctx.B;
        if (state.Params.TryGetValue("anim", out var animEntry))
            LinkAnim(ctx, state.Id, ExprParser.Parse(animEntry.Value), SourceRef.At(state.FileId, animEntry.Line), null);

        if (state.Params.TryGetValue("poweradd", out var pe) && ConstNumber(ExprParser.Parse(pe.Value)) is { } cost)
        {
            EnsurePower(b);
            b.Relate(RelationKind.ResourceCost, state.Id, "resource:power", "power.literal-cost", SourceRef.At(state.FileId, pe.Line),
                props: [new("amount", Fmt(cost)), new("origin", "statedef")]);
        }
    }

    // ------------------------------------------------------------------ Gates

    private static void LinkGate(LinkContext ctx, ControllerData c)
    {
        var b = ctx.B;
        var commandsDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var powerDone = new HashSet<string>(StringComparer.Ordinal);
        var lines = c.Gate.TriggerAll.Concat(c.Gate.Branches.SelectMany(br => br.Lines)).ToList();
        var anyAi = false;

        foreach (var line in lines)
        {
            if (line.Expression is RawExpr) c.Obj.Props["unparsed"] = "true";
            var facts = ExprAnalyzer.Analyze(line.Expression);
            if (facts.ReadsAiLevel) anyAi = true;

            foreach (var cmd in facts.Commands)
            {
                if (!commandsDone.Add(cmd.Name)) continue;
                if (ctx.Commands.TryGetValue(cmd.Name, out var cmdId))
                {
                    b.Relate(RelationKind.ReferencesCommand, c.Id, cmdId, "gate.command", line.Source,
                        props: cmd.Negated ? [new("negated", "true")] : null);
                }
                else
                {
                    var id = $"cmd:{cmd.Name}";
                    b.Stub(ObjectKind.Command, id, cmd.Name, "not defined in the CMD");
                    b.Relate(RelationKind.ReferencesCommand, c.Id, id, "command.undefined", line.Source, note: "command name is not defined");
                }
            }

            foreach (var p in facts.Power)
            {
                if (!powerDone.Add(p.Op + p.Value.ToString(CultureInfo.InvariantCulture))) continue;
                EnsurePower(b);
                b.Relate(RelationKind.GatedByPower, c.Id, "resource:power", "power.literal-gate", line.Source,
                    props: [new("op", p.Op), new("value", Fmt(p.Value))]);
            }
        }

        if (anyAi)
        {
            c.Obj.Props["readsAILevel"] = "true";
            b.Relate(RelationKind.EntryPoint, ctx.CharacterId, c.Id, "entry.ai-trigger", c.Span, props: [new("kind", "ai")]);
        }
    }

    // ------------------------------------------------------------------ Controllers

    private static void LinkController(LinkContext ctx, ControllerData c)
    {
        var b = ctx.B;
        switch (c.Type)
        {
            case "changestate" or "selfstate":
                LinkStateChange(ctx, c);
                break;

            case "hitdef" or "reversaldef" or "projectile":
                LinkHitDef(ctx, c);
                if (c.Type == "projectile") LinkProjectile(ctx, c);
                break;

            case "helper":
                LinkHelper(ctx, c);
                break;

            case "targetstate":
                if (ctx.Param(c, "value") is { } tv)
                    LinkStateTarget(ctx, c.Id, RelationKind.SetsVictimState, tv, ctx.Line(c, "value"), c.Gate, "state.literal-victim");
                break;

            case "targetbind":
                b.Add(ObjectKind.Entity, "entity:target", "target");
                b.Relate(RelationKind.Binds, c.Id, "entity:target", "bind.target", c.Span);
                break;

            case "targetfacing" or "targetlifeadd" or "targetpoweradd" or "targetveladd" or "targetvelset" or "targetdrop":
                b.Add(ObjectKind.Entity, "entity:target", "target");
                b.Relate(RelationKind.AffectsTarget, c.Id, "entity:target", "target.affect", c.Span, props: [new("controller", c.Type)]);
                break;

            case "changeanim" or "changeanim2":
                if (ctx.Param(c, "value") is { } av) LinkAnim(ctx, c.Id, av, ctx.Line(c, "value"), c.Gate);
                break;

            case "poweradd":
                if (ctx.Param(c, "value") is { } pv && ConstNumber(pv) is { } amount)
                {
                    EnsurePower(b);
                    b.Relate(RelationKind.ResourceCost, c.Id, "resource:power", "power.literal-cost", ctx.Line(c, "value"),
                        props: [new("amount", Fmt(amount)), new("origin", "poweradd")]);
                }

                break;
        }

        LinkTimeline(ctx, c);
    }

    private static void LinkStateChange(LinkContext ctx, ControllerData c)
    {
        var value = ctx.Param(c, "value");
        if (value is null)
        {
            ctx.B.Warn("state.no-target", $"{c.Type} in {c.Id} has no value=.", c.Span);
            return;
        }

        LinkStateTarget(ctx, c.Id, RelationKind.ChangesState, value, ctx.Line(c, "value"), c.Gate, "state.literal-change");
    }

    /// <summary>Adds an edge from <paramref name="from"/> to the state the expression names, with the right confidence.</summary>
    private static void LinkStateTarget(LinkContext ctx, string from, RelationKind kind, Expr target, SourceRef source, Gate? gate, string literalRule)
    {
        var b = ctx.B;
        if (ConstNumber(target) is { } number && Math.Abs(number - Math.Round(number)) < 1e-9)
        {
            var n = (int)Math.Round(number);
            var (id, rule) = ResolveState(ctx, n);
            b.Relate(kind, from, id, rule == "state.literal-change" ? literalRule : rule, source, gate);
            return;
        }

        var text = target is RawExpr raw ? raw.Text : ExprPrinter.ToSExpr(target);
        var dyn = $"dynamic:{from}";
        var stub = b.Stub(ObjectKind.State, dyn, "dynamic target", "target is an expression");
        stub.Props["expr"] = text;
        b.Relate(kind, from, dyn, target is RawExpr ? "expr.unparsed" : "state.dynamic-target", source, gate, note: text);
    }

    private static (string Id, string Rule) ResolveState(LinkContext ctx, int n)
    {
        if (ctx.States.Effective.TryGetValue(n, out var s)) return (s.Id, "state.literal-change");
        var id = $"state:{n}";
        var engineCommon = n is >= 0 and < 200 || n is >= 5000 and < 6000;
        if (!ctx.CommonLoaded && engineCommon)
        {
            var stub = ctx.B.Stub(ObjectKind.State, id, $"State {n} (engine common)", "common state not loaded");
            stub.Props["engineCommon"] = "true";
            return (id, "state.engine-common");
        }

        ctx.B.Stub(ObjectKind.State, id, $"State {n} (undefined)", "not defined in any indexed file");
        return (id, "state.missing");
    }

    private static void LinkHitDef(LinkContext ctx, ControllerData c)
    {
        var b = ctx.B;
        var id = c.Id + "/hitdef";
        var hit = b.Add(ObjectKind.HitDef, id, c.Type == "reversaldef" ? "ReversalDef" : c.Type == "projectile" ? "Projectile hit" : "HitDef", c.Span, c.Id);
        foreach (var e in c.ParamList)
        {
            var key = e.Key.ToLowerInvariant();
            if (key is "type" or "persistent" or "ignorehitpause" or "name") continue;
            hit.Props["p." + key] = e.Value;
        }

        hit.Props["controller"] = c.Type;
        b.Relate(RelationKind.DefinesHitDef, c.Id, id, "structure.contains", c.Span);

        if (ctx.Param(c, "p2stateno") is { } p2)
            LinkStateTarget(ctx, id, RelationKind.SetsVictimState, p2, ctx.Line(c, "p2stateno"), c.Gate, "state.literal-victim");
        if (ctx.Param(c, "p1stateno") is { } p1)
            LinkStateTarget(ctx, id, RelationKind.SetsAttackerState, p1, ctx.Line(c, "p1stateno"), c.Gate, "state.literal-attacker");
    }

    private static void LinkProjectile(LinkContext ctx, ControllerData c)
    {
        var b = ctx.B;
        var idExpr = ctx.Param(c, "projid");
        var n = idExpr is null ? 0 : ConstNumber(idExpr) is { } v ? (int)v : (int?)null;
        var projId = n is null ? $"proj:dynamic@{c.Id}" : $"proj:{n}";
        var proj = b.Add(ObjectKind.Projectile, projId, n is null ? "Projectile (dynamic id)" : $"Projectile {n}");
        b.Relate(RelationKind.SpawnsProjectile, c.Id, projId, n is null ? "helper.dynamic-spawn" : "projectile.spawn", c.Span, c.Gate);
        foreach (var key in new[] { "projanim", "projhitanim", "projremanim", "projcancelanim" })
        {
            if (ctx.Param(c, key) is not { } anim) continue;
            LinkAnim(ctx, projId, anim, ctx.Line(c, key), null, key);
        }

        proj.Props.TryAdd("spawnedBy", c.Id);
    }

    private static void LinkHelper(LinkContext ctx, ControllerData c)
    {
        var b = ctx.B;
        var idExpr = ctx.Param(c, "id");
        var stateExpr = ctx.Param(c, "stateno");
        int? id = idExpr is null ? 0 : ConstNumber(idExpr) is { } iv ? (int)iv : null;
        int? stateno = stateExpr is null ? 0 : ConstNumber(stateExpr) is { } sv ? (int)sv : null;

        var helperId = id is null ? $"helper:dynamic@{c.Id}" : $"helper:{id}";
        var helper = b.Add(ObjectKind.Helper, helperId, id is null ? "Helper (dynamic id)" : $"Helper {id}");
        if (c.Params.TryGetValue("name", out var nameEntry) && helper.Prop("name") is null) helper.Props["name"] = nameEntry.Value.Trim('"');
        if (c.Params.TryGetValue("helpertype", out var ht)) helper.Props["helpertype"] = ht.Value;

        var literal = id is not null && stateno is not null;
        b.Relate(RelationKind.SpawnsHelper, c.Id, helperId, literal ? "helper.literal-spawn" : "helper.dynamic-spawn", c.Span, c.Gate);
        if (stateno is not null)
        {
            var (target, rule) = ResolveState(ctx, stateno.Value);
            b.Relate(RelationKind.HelperRunsState, helperId, target, rule == "state.literal-change" ? "helper.literal-spawn" : rule, ctx.Line(c, "stateno"),
                props: [new("spawnedBy", c.Id)]);
            var existing = helper.Prop("statenos");
            var list = string.IsNullOrEmpty(existing) ? new List<string>() : existing.Split(',').ToList();
            if (!list.Contains(stateno.Value.ToString(CultureInfo.InvariantCulture))) list.Add(stateno.Value.ToString(CultureInfo.InvariantCulture));
            helper.Props["statenos"] = string.Join(",", list);
        }
        else
        {
            b.Stub(ObjectKind.State, $"dynamic:{c.Id}#helper", "dynamic helper state", "stateno is an expression");
            b.Relate(RelationKind.HelperRunsState, helperId, $"dynamic:{c.Id}#helper", "helper.dynamic-spawn", ctx.Line(c, "stateno"));
        }
    }

    // ------------------------------------------------------------------ Animation

    private static void LinkAnim(LinkContext ctx, string from, Expr value, SourceRef source, Gate? gate, string? role = null)
    {
        var b = ctx.B;
        var props = role is null ? null : new[] { new KeyValuePair<string, string>("role", role) };
        if (ConstNumber(value) is { } n && Math.Abs(n - Math.Round(n)) < 1e-9)
        {
            var num = (int)Math.Round(n);
            if (ctx.AnimIds.TryGetValue(num, out var animId))
            {
                b.Relate(RelationKind.UsesAnim, from, animId, "anim.literal-ref", source, gate, props: props);
            }
            else
            {
                var id = $"anim:{num}";
                b.Stub(ObjectKind.Animation, id, $"Action {num} (undefined)", "not defined in the AIR");
                b.Relate(RelationKind.UsesAnim, from, id, "anim.missing", source, gate, props: props);
            }

            return;
        }

        var dyn = $"dynamic:{from}#anim";
        b.Stub(ObjectKind.Animation, dyn, "dynamic animation", "animation is an expression").Props["expr"] = ExprPrinter.ToSExpr(value);
        b.Relate(RelationKind.UsesAnim, from, dyn, value is RawExpr ? "expr.unparsed" : "anim.dynamic", source, gate, props: props);
    }

    /// <summary>Maps AnimElem/Time gates onto animation frames so the timeline can place the controller.</summary>
    private static void LinkTimeline(LinkContext ctx, ControllerData c)
    {
        if (c.Type is not ("hitdef" or "reversaldef" or "projectile" or "helper" or "changestate" or "selfstate")) return;
        var state = c.State;
        if (!state.Params.TryGetValue("anim", out var animEntry)) return;
        if (ConstNumber(ExprParser.Parse(animEntry.Value)) is not { } an || !ctx.AnimIds.TryGetValue((int)an, out var animId)) return;
        if (!ctx.AnimFrames.TryGetValue(animId, out var frames) || frames.Count == 0) return;

        var changesAnim = state.Controllers.Any(o => o.Type is "changeanim" or "changeanim2");
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in c.Gate.Branches)
        {
            foreach (var elem in branch.Facets.AnimElem.Where(e => e.Op == "="))
            {
                var index = (int)elem.Value - 1;
                if (index < 0 || index >= frames.Count || !done.Add(frames[index].Id)) continue;
                ctx.B.Relate(RelationKind.ActiveAtFrame, c.Id, frames[index].Id,
                    changesAnim ? "timeline.animelem-mixed" : "timeline.animelem-literal", c.Span,
                    props: [new("gate", $"AnimElem = {(int)elem.Value}")]);
            }

            foreach (var t in branch.Facets.Time.Where(t => t.Op is "=" or ">="))
            {
                var tick = (int)t.Value;
                var index = frames.FindLastIndex(f => f.StartTick <= tick);
                if (index < 0 || !done.Add(frames[index].Id)) continue;
                ctx.B.Relate(RelationKind.ActiveAtFrame, c.Id, frames[index].Id, "timeline.time-gate", c.Span,
                    props: [new("gate", $"Time {t.Op} {tick}")]);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    public static void EnsurePower(IndexBuilder b)
    {
        if (b.Find("resource:power") is null) b.Add(ObjectKind.Resource, "resource:power", "Power");
    }

    /// <summary>Folds literal arithmetic (<c>200+1</c>, <c>-1</c>); null when anything non-constant is involved.</summary>
    public static double? ConstNumber(Expr e)
    {
        switch (e)
        {
            case NumberLit n: return n.Value;
            case Unary { Op: "-" } u: return -ConstNumber(u.Operand);
            case Unary { Op: "+" } u2: return ConstNumber(u2.Operand);
            case Binary b:
                var l = ConstNumber(b.Left);
                var r = ConstNumber(b.Right);
                if (l is null || r is null) return null;
                return b.Op switch
                {
                    "+" => l + r,
                    "-" => l - r,
                    "*" => l * r,
                    "/" when r != 0 => l / r,
                    "%" when r != 0 => l % r,
                    "**" => Math.Pow(l.Value, r.Value),
                    _ => null
                };
            default: return null;
        }
    }

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
