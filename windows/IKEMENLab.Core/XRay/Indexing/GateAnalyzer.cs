using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Indexing;

/// <summary>
/// Turns a controller's trigger lines into <see cref="GateFacets"/>. Only exact, literal shapes are recognised
/// (<c>command = "x"</c>, <c>power &gt;= 1000</c>, <c>MoveContact</c>…). Anything else is counted in
/// <see cref="GateFacets.OtherConditions"/>, so a consumer can tell when the facets are incomplete.
/// </summary>
public static class GateAnalyzer
{
    private const int MaxAlternatives = 16;

    /// <summary>One facets record per alternative: OR-ed command tests such as <c>command = "a" || command = "b"</c> expand into separate alternatives.</summary>
    public static IReadOnlyList<GateFacets> Expand(IReadOnlyList<TriggerLine> triggerAll, IReadOnlyList<TriggerLine> branch)
    {
        var conjuncts = triggerAll.Concat(branch).SelectMany(l => ExprAnalyzer.Conjuncts(l.Expression)).ToList();

        // Alternatives for the command choice; each starts with no commands.
        var commandAlternatives = new List<List<CommandRef>> { new() };
        var simple = new List<Expr>();
        foreach (var c in conjuncts)
        {
            var orCommands = OrOfCommands(c);
            if (orCommands is { Count: > 1 })
            {
                var next = new List<List<CommandRef>>();
                foreach (var existing in commandAlternatives)
                    foreach (var option in orCommands)
                        next.Add([.. existing, option]);
                commandAlternatives = next.Count <= MaxAlternatives ? next : commandAlternatives;
            }
            else
            {
                simple.Add(c);
            }
        }

        return commandAlternatives.Select(cmds => Facets(simple, cmds)).ToList();
    }

    private static GateFacets Facets(IReadOnlyList<Expr> conjuncts, List<CommandRef> extraCommands)
    {
        var commands = extraCommands.Where(c => !c.Negated).Select(c => c.Name).ToList();
        var negated = extraCommands.Where(c => c.Negated).Select(c => c.Name).ToList();
        var contact = new List<string>();
        var power = new List<NumCompare>();
        var time = new List<NumCompare>();
        var animElem = new List<NumCompare>();
        var stateTypes = new List<string>();
        var moveTypes = new List<string>();
        var ctrl = false;
        var ai = false;
        var other = 0;
        var unmodelled = new List<string>();
        List<StateRange>? source = null;
        var excluded = new List<StateRange>();
        List<StateRange>? prev = null;

        foreach (var c in conjuncts)
        {
            var f = ExprAnalyzer.Analyze(c);
            if (IsConstantTrue(c)) continue;

            if (StateConstraint(c, "stateno") is { } sc)
            {
                if (sc.Negated) excluded.AddRange(sc.Ranges);
                else source = source is null ? sc.Ranges : StateRange.Intersect(source, sc.Ranges);
                continue;
            }

            if (StateConstraint(c, "prevstateno") is { Negated: false } pc)
            {
                prev = prev is null ? pc.Ranges : StateRange.Intersect(prev, pc.Ranges);
                continue;
            }

            if (c is Binary { Op: "=" or "!=", Left: Ident { Name: "command" }, Right: StringLit } && f.Commands.Count == 1)
            {
                (f.Commands[0].Negated ? negated : commands).Add(f.Commands[0].Name);
            }
            else if (c is Ident { Name: "ctrl" } || c is Binary { Op: "=", Left: Ident { Name: "ctrl" }, Right: NumberLit { Value: 1 } })
            {
                ctrl = true;
            }
            else if (c is Ident { Name: "movehit" or "movecontact" or "moveguarded" or "movereversed" } cid)
            {
                contact.Add(cid.Name);
            }
            else if (c is Binary { Left: Ident { Name: "movehit" or "movecontact" or "moveguarded" or "movereversed" } lid, Right: NumberLit } cb &&
                     cb.Op is "=" or ">" or ">=")
            {
                contact.Add(lid.Name);
            }
            else if (c is Ident { Name: "ailevel" } || c is Binary { Left: Ident { Name: "ailevel" }, Right: NumberLit })
            {
                ai = true;
            }
            else if (IsPureCompare(c, "power") && f.Power.Count == 1) power.Add(f.Power[0]);
            else if (IsPureCompare(c, "time") && f.Time.Count == 1) time.Add(f.Time[0]);
            else if (IsPureCompare(c, "animelem") && f.AnimElems.Count == 1) animElem.Add(f.AnimElems[0]);
            else if (c is Binary { Op: "=" or "!=", Left: Ident { Name: "statetype" }, Right: Ident } && f.StateTypes.Count == 1) stateTypes.Add(f.StateTypes[0]);
            else if (c is Binary { Op: "=" or "!=", Left: Ident { Name: "movetype" }, Right: Ident } && f.MoveTypes.Count == 1) moveTypes.Add(f.MoveTypes[0]);
            else if (OrOfIdentEquals(c, "statetype") is { Count: > 0 } sts) stateTypes.AddRange(sts);
            else if (OrOfIdentEquals(c, "movetype") is { Count: > 0 } mts) moveTypes.AddRange(mts);
            else
            {
                other++;
                unmodelled.Add(ExprPrinter.ToSExpr(c));
            }
        }

        return new GateFacets(
            commands.Distinct().ToList(), negated.Distinct().ToList(), contact.Distinct().ToList(),
            power, time, animElem, ctrl, stateTypes.Distinct().ToList(), moveTypes.Distinct().ToList(), ai, other,
            source, excluded, prev, unmodelled);
    }

    private sealed record StateConstraintShape(List<StateRange> Ranges, bool Negated);

    /// <summary>
    /// Recognises <c>stateno = 200</c>, <c>= [200,210]</c> / <c>(200,210]</c>, <c>!=</c> of those, <c>&gt;= 1000</c>-style bounds,
    /// and an OR of positive equalities. Anything else (arithmetic, other triggers inside) is not recognised.
    /// </summary>
    private static StateConstraintShape? StateConstraint(Expr e, string trigger)
    {
        static int? Int(Expr x) => x switch
        {
            NumberLit { IsInt: true } n => (int)n.Value,
            Unary { Op: "-", Operand: NumberLit { IsInt: true } n } => -(int)n.Value,
            _ => null
        };

        List<StateRange>? Equality(Expr x)
        {
            if (x is Binary { Op: "=", Left: Ident l } b && l.Name == trigger)
            {
                if (Int(b.Right) is { } n) return [new StateRange(n, n)];
                if (b.Right is Interval { Low: var lo, High: var hi } iv && Int(lo) is { } a && Int(hi) is { } z)
                {
                    var from = iv.Open == '(' ? a + 1 : a;
                    var to = iv.Close == ')' ? z - 1 : z;
                    return from <= to ? [new StateRange(from, to)] : [];
                }
            }

            if (x is Binary { Op: "||" } or)
            {
                var left = Equality(or.Left);
                var right = Equality(or.Right);
                if (left is null || right is null) return null;
                return [.. left, .. right];
            }

            return null;
        }

        if (Equality(e) is { } eq) return new StateConstraintShape(eq, false);

        if (e is Binary { Op: "!=", Left: Ident l } ne && l.Name == trigger)
        {
            if (Int(ne.Right) is { } n) return new StateConstraintShape([new StateRange(n, n)], true);
            if (ne.Right is Interval { Low: var lo, High: var hi } iv && Int(lo) is { } a && Int(hi) is { } z)
                return new StateConstraintShape([new StateRange(iv.Open == '(' ? a + 1 : a, iv.Close == ')' ? z - 1 : z)], true);
        }

        if (e is Binary { Op: ">=" or ">" or "<=" or "<", Left: Ident bl, Right: var rhs } cmp && bl.Name == trigger && Int(rhs) is { } bound)
        {
            var range = cmp.Op switch
            {
                ">=" => new StateRange(bound, int.MaxValue),
                ">" => new StateRange(bound + 1, int.MaxValue),
                "<=" => new StateRange(int.MinValue, bound),
                _ => new StateRange(int.MinValue, bound - 1)
            };
            return new StateConstraintShape([range], false);
        }

        return null;
    }

    private static bool IsConstantTrue(Expr e) => e is NumberLit { Value: not 0 } || e is Binary { Op: "=", Left: NumberLit l, Right: NumberLit r } && l.Value == r.Value;

    private static bool IsPureCompare(Expr e, string name) =>
        e is Binary { Op: "=" or "!=" or "<" or "<=" or ">" or ">=" } b &&
        ((b.Left is Ident i && i.Name == name && (b.Right is NumberLit || b.Right is ExprList { Items: [NumberLit, ..] } ||
                                                   b.Right is Unary { Operand: NumberLit })) ||
         (b.Right is Ident j && j.Name == name && b.Left is NumberLit));

    /// <summary>The commands of a chain of <c>command = "x" || command = "y"</c>, or null when the expression is anything else.</summary>
    private static List<CommandRef>? OrOfCommands(Expr e)
    {
        var refs = new List<CommandRef>();
        bool Go(Expr x)
        {
            switch (x)
            {
                case Binary { Op: "||" } or:
                    return Go(or.Left) && Go(or.Right);
                case Binary { Op: "=" or "!=", Left: Ident { Name: "command" }, Right: StringLit s } eq:
                    refs.Add(new CommandRef(s.Value, eq.Op == "!="));
                    return true;
                default:
                    return false;
            }
        }

        return e is Binary { Op: "||" } && Go(e) ? refs : null;
    }

    private static List<string>? OrOfIdentEquals(Expr e, string trigger)
    {
        var values = new List<string>();
        bool Go(Expr x)
        {
            switch (x)
            {
                case Binary { Op: "||" } or:
                    return Go(or.Left) && Go(or.Right);
                case Binary { Op: "=", Left: Ident l, Right: Ident r } when l.Name == trigger:
                    values.Add(r.Name);
                    return true;
                default:
                    return false;
            }
        }

        return e is Binary { Op: "||" } && Go(e) ? values : null;
    }
}
