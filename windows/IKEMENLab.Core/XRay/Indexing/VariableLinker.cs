using System.Globalization;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Indexing;

/// <summary>
/// Reads and writes of var/fvar/sysvar/sysfvar: from trigger and parameter expressions (including redirects and
/// <c>:=</c>) and from the VarSet family of controllers. Only literal indexes are StaticProven.
/// </summary>
internal static class VariableLinker
{
    private static readonly HashSet<string> SetFamily = new(StringComparer.Ordinal)
    {
        "varset", "varadd", "varrandom", "varrangeset", "parentvarset", "parentvaradd", "rootvarset", "rootvaradd"
    };

    private static readonly HashSet<string> StructuralKeys = new(StringComparer.Ordinal) { "type", "persistent", "ignorehitpause", "name" };

    public static void Link(LinkContext ctx)
    {
        foreach (var state in ctx.States.States)
        {
            foreach (var (key, entry) in state.Params)
            {
                if (key is "type" or "movetype" or "physics") continue;
                Reads(ctx, state.Id, ExprParser.Parse(entry.Value), SourceRef.At(state.FileId, entry.Line), "statedef");
            }
        }

        foreach (var c in ctx.States.Controllers)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in c.Gate.TriggerAll.Concat(c.Gate.Branches.SelectMany(b => b.Lines)))
                Reads(ctx, c.Id, line.Expression, line.Source ?? c.Span, "trigger", seen);

            var isSet = SetFamily.Contains(c.Type);
            foreach (var e in c.ParamList)
            {
                var key = e.Key.ToLowerInvariant();
                if (StructuralKeys.Contains(key)) continue;
                var line = SourceRef.At(c.FileId, e.Line);
                if (isSet && TargetOf(key) is not null) continue; // handled as the write target below
                Reads(ctx, c.Id, ExprParser.Parse(e.Value), line, "param", seen);
            }

            if (isSet) LinkSetController(ctx, c);
        }
    }

    private static void LinkSetController(LinkContext ctx, ControllerData c)
    {
        var scope = c.Type.StartsWith("parent", StringComparison.Ordinal) ? EntityScope.Parent
            : c.Type.StartsWith("root", StringComparison.Ordinal) ? EntityScope.Root
            : EntityScope.Self;
        var isAdd = c.Type.EndsWith("add", StringComparison.Ordinal);

        if (c.Type == "varrangeset")
        {
            LinkRangeSet(ctx, c, scope);
            return;
        }

        var wrote = false;
        foreach (var e in c.ParamList)
        {
            var key = e.Key.ToLowerInvariant();
            var line = SourceRef.At(c.FileId, e.Line);

            // Form 1: "var(5) = 100" / "fvar(2) = 1.5"
            if (TargetOf(key) is { } t)
            {
                Write(ctx, c.Id, t.Kind, t.Index, scope, null, line, ExprParser.Parse(e.Value), isAdd);
                wrote = true;
                continue;
            }

            // Form 2: "v = 5" (int var) / "fv = 3" (float var), value in "value"
            if (key is "v" or "fv")
            {
                var kind = key == "v" ? VarKind.Var : VarKind.FVar;
                int? index = ExprParser.Parse(e.Value) is { } ie && StateLinker.ConstNumber(ie) is { } iv ? (int)iv : null;
                var value = c.Params.TryGetValue("value", out var ve) ? ExprParser.Parse(ve.Value) : null;
                Write(ctx, c.Id, kind, index, scope, null, line, value, isAdd);
                wrote = true;
            }
        }

        if (!wrote) ctx.B.Warn("var.no-target", $"{c.Type} in {c.Id} names no variable.", c.Span, DiagnosticSeverity.Info);
    }

    private static void LinkRangeSet(LinkContext ctx, ControllerData c, EntityScope scope)
    {
        var first = ctx.Param(c, "first") is { } f ? StateLinker.ConstNumber(f) : 0;
        var last = ctx.Param(c, "last") is { } l ? StateLinker.ConstNumber(l) : 59;
        var floatRange = c.Params.ContainsKey("fvalue") && !c.Params.ContainsKey("value");
        var valueExpr = ctx.Param(c, floatRange ? "fvalue" : "value");
        var kind = floatRange ? VarKind.FVar : VarKind.Var;
        var line = c.Span;

        if (first is null || last is null || last < first || last - first > 200)
        {
            Write(ctx, c.Id, kind, null, scope, null, line, valueExpr, false);
            return;
        }

        for (var i = (int)first.Value; i <= (int)last.Value; i++)
            Write(ctx, c.Id, kind, i, scope, null, line, valueExpr, false, viaRange: true);
    }

    private static void Write(LinkContext ctx, string from, VarKind kind, int? index, EntityScope scope, string? scopeArg,
        SourceRef line, Expr? value, bool isAdd, bool viaRange = false)
    {
        var b = ctx.B;
        var varId = VarObject(b, kind, index);
        var literal = value is null ? null : StateLinker.ConstNumber(value);
        var props = new List<KeyValuePair<string, string>> { new("scope", scope.ToString().ToLowerInvariant()), new("via", viaRange ? "range" : "varset") };
        if (scopeArg is not null) props.Add(new("scopeArg", scopeArg));
        if (literal is not null) props.Add(new(isAdd ? "step" : "value", literal.Value.ToString("0.####", CultureInfo.InvariantCulture)));
        b.Relate(RelationKind.WritesVar, from, varId, index is null ? "var.dynamic-index" : "var.literal-index", line, props: props);

        if (!isAdd && literal == 0 && index is not null && scope == EntityScope.Self)
            b.Relate(RelationKind.ResetsVar, from, varId, "var.literal-reset", line, props: [new("scope", "self")]);
    }

    private static void Reads(LinkContext ctx, string from, Expr expr, SourceRef line, string via, HashSet<string>? seen = null)
    {
        var b = ctx.B;
        var facts = ExprAnalyzer.Analyze(expr);
        foreach (var v in facts.Vars)
        {
            var key = $"{v.IsWrite}|{v.Kind}|{v.Index}|{v.Scope}|{v.ScopeArg}";
            if (seen is not null && !seen.Add(key)) continue;
            var varId = VarObject(b, v.Kind, v.Index);
            var props = new List<KeyValuePair<string, string>> { new("scope", v.Scope.ToString().ToLowerInvariant()), new("via", via) };
            if (v.ScopeArg is not null) props.Add(new("scopeArg", v.ScopeArg));
            var rule = v.Index is null ? "var.dynamic-index" : "var.literal-index";
            b.Relate(v.IsWrite ? RelationKind.WritesVar : RelationKind.ReadsVar, from, varId, rule, line, props: props);

            if (v.IsWrite && v.Index is not null && v.Scope == EntityScope.Self && expr is Assign { Value: NumberLit { Value: 0 } })
                b.Relate(RelationKind.ResetsVar, from, varId, "var.literal-reset", line, props: [new("scope", "self")]);
        }
    }

    private static string VarObject(IndexBuilder b, VarKind kind, int? index)
    {
        var kindName = kind.ToString().ToLowerInvariant();
        var id = $"var:{kindName}:{(index is null ? "*" : index.Value.ToString(CultureInfo.InvariantCulture))}";
        var obj = b.Add(ObjectKind.Variable, id, index is null ? $"{kindName}(?)" : $"{kindName}({index})");
        obj.Props.TryAdd("kind", kindName);
        if (index is not null) obj.Props.TryAdd("index", index.Value.ToString(CultureInfo.InvariantCulture));
        else obj.Props.TryAdd("dynamic", "true");
        return id;
    }

    /// <summary>Parses "var(5)" style parameter keys.</summary>
    private static (VarKind Kind, int? Index)? TargetOf(string key)
    {
        foreach (var (name, kind) in new[] { ("sysfvar", VarKind.SysFVar), ("sysvar", VarKind.SysVar), ("fvar", VarKind.FVar), ("var", VarKind.Var) })
        {
            if (!key.StartsWith(name + "(", StringComparison.Ordinal) || !key.EndsWith(')')) continue;
            var inner = key[(name.Length + 1)..^1].Trim();
            var e = ExprParser.Parse(inner);
            return (kind, StateLinker.ConstNumber(e) is { } n ? (int)n : null);
        }

        return null;
    }
}
