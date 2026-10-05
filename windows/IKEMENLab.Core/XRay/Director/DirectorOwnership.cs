using System.Globalization;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Director;

public enum OwnershipVerdict
{
    /// <summary>The generated behavior is evaluated first; nothing can take the fighter out of its chase.</summary>
    Owned,
    /// <summary>The existing rules listed keep priority (an explicit choice); the behavior acts only when none of them changes state first.</summary>
    OwnedAfterExisting,
    /// <summary>Ownership cannot be established safely; promotion is refused with the reasons.</summary>
    Refused
}

/// <summary>How an existing rule relates to the knockdown opportunity.</summary>
/// <param name="Relation">same-opportunity (it reads a knockdown), command (the engine's AI may press its command), unknown (its conditions are not
/// readable — variables, random…), disjoint, human-only, continuation (it only fires from specific move states).</param>
/// <param name="StealsChase">It can change state while the generated chase runs (no control requirement and nothing that excludes the chase state).</param>
public sealed record OwnershipRow(
    string ControllerId, string StateId, string Name, string Target, string Relation, bool BeforeOurs, bool StealsChase, bool Suppressed, bool Suppressible,
    string File, int Line, string Plain);

/// <param name="Rows">Every existing state change that can matter (competes for the opportunity or can interrupt the chase), in file order.</param>
public sealed record OwnershipReport(
    OwnershipVerdict Verdict, string Summary, IReadOnlyList<OwnershipRow> Rows, IReadOnlyList<string> Reasons, string? MinusOneFile)
{
    public bool Refused => Verdict == OwnershipVerdict.Refused;
    public IReadOnlyList<OwnershipRow> Competitors => Rows.Where(r => r.Relation is "same-opportunity" or "command" or "unknown").ToList();
    public IReadOnlyList<OwnershipRow> StealRisks => Rows.Where(r => r.StealsChase).ToList();
}

/// <summary>
/// One owner per decision. Before a generated behavior is promoted, every existing State -1 (and -2/-3) state change is read for two questions:
/// can it take the same knockdown opportunity (and is it before or after the generated rule), and can it change state while the generated chase runs?
/// Only literal shapes are read (the Phase 5 condition vocabulary and the gate facets); anything unreadable is "unknown", never assumed harmless.
/// </summary>
public static class DirectorOwnership
{
    /// <summary>States a fighter with control is typically standing or walking in. A rule restricted to other states is a continuation, not a neutral decision.</summary>
    private static readonly int[] NeutralStates = [0, 10, 11, 12, 20, 21, 52];

    public static OwnershipReport Analyze(SemanticIndex index, CharacterFacts facts, KnockdownChaseSpec spec)
    {
        var reasons = new List<string>();
        if (facts.MinusOneDeclaredIn.Count == 0)
            reasons.Add("No loaded file declares [Statedef -1], so there is no place for the generated decision.");
        else if (facts.MinusOneDeclaredIn.Count > 1)
            reasons.Add($"[Statedef -1] is declared in {string.Join(" and ", facts.MinusOneDeclaredIn)}; which one the engine uses depends on its merge rules, so ownership cannot be established.");

        var suppress = spec.Placement == OwnershipPlacement.SuppressSpecific ? spec.Suppress.ToHashSet(StringComparer.Ordinal) : [];
        var rows = new List<OwnershipRow>();
        foreach (var hub in new[] { "state:-1", "state:-2", "state:-3" })
        {
            foreach (var c in index.ControllersOf(hub))
            {
                var type = (c.Prop("type") ?? string.Empty).ToLowerInvariant();
                if (type is not ("changestate" or "selfstate")) continue;
                if (c.Name.StartsWith(DirectorCompiler.RulePrefix, StringComparison.Ordinal)) continue;   // the generated rules themselves
                var file = c.Source is { } src && src.FileId < index.Files.Count ? index.Files[src.FileId].RelPath : "?";
                var inMinusOneFile = hub == "state:-1" && facts.MinusOneFile is { } mf && file.EndsWith("/" + mf, StringComparison.OrdinalIgnoreCase) | file.Equals(mf, StringComparison.OrdinalIgnoreCase);
                var relation = Relation(c, spec);
                var steals = StealsChase(index, c, facts.ChaseState);
                if (relation is "disjoint" or "human-only" or "continuation" && !steals) continue;
                var target = index.Outgoing(c.Id, RelationKind.ChangesState).Select(r => r.To).FirstOrDefault() ?? "?";
                var before = hub != "state:-1" || spec.Placement == OwnershipPlacement.AfterExisting;   // -2/-3 run before -1 every tick
                var name = c.Name;
                var suppressed = suppress.Contains(c.Id);
                rows.Add(new OwnershipRow(c.Id, hub, name, target, relation, before, steals, suppressed, inMinusOneFile, file, c.Source?.StartLine ?? 0,
                    Plain(index, c, hub, relation, steals, target, before, suppressed)));
            }
        }

        foreach (var id in suppress.Where(id => rows.All(r => r.ControllerId != id)))
            reasons.Add($"{id} cannot be suppressed: it is not an existing State -1 state change that competes here.");
        foreach (var r in rows.Where(r => r.Suppressed && !r.Suppressible))
            reasons.Add($"{r.Name} ({r.ControllerId}) cannot be suppressed: only State -1 rules in {facts.MinusOneFile} can be (it lives in {r.File}).");
        foreach (var r in rows.Where(r => r.StealsChase && !(r.Suppressed && r.Suppressible)))
            reasons.Add($"{r.Name} ({r.ControllerId}, {r.File}:{r.Line}) can change state while the chase runs — it does not require control and nothing in it excludes the chase state" +
                        (r.Suppressible ? "; suppress it, or the chase can be taken over." : "; it is outside State -1 and cannot be suppressed here."));

        var verdict = reasons.Count > 0 ? OwnershipVerdict.Refused
            : spec.Placement == OwnershipPlacement.AfterExisting ? OwnershipVerdict.OwnedAfterExisting : OwnershipVerdict.Owned;
        var competing = rows.Count(r => r.Relation is "same-opportunity" or "command" or "unknown");
        var summary = verdict switch
        {
            OwnershipVerdict.Refused => "Refused — ownership cannot be established safely: " + reasons[0],
            OwnershipVerdict.OwnedAfterExisting => competing == 0
                ? "Owned — no existing rule competes for the knockdown, and nothing can interrupt the chase."
                : $"Existing rules keep priority ({competing} can act on the knockdown first); the generated behavior acts only when none of them does. Nothing can interrupt the chase once it starts.",
            _ => (competing == 0 ? "Owned — no existing rule competes for the knockdown" : $"Owned — evaluated before the {competing} existing rule(s) that could act on the knockdown")
                 + (suppress.Count > 0 ? $"; {suppress.Count} suppressed for knockdowns" : string.Empty) + ", and nothing can interrupt the chase."
        };
        return new OwnershipReport(verdict, summary, rows, reasons, facts.MinusOneFile);
    }

    /// <summary>The rule's relation to "the fighter can act on the ground, the opponent is down" (the opportunity tick), from its most permissive branch.</summary>
    internal static string Relation(SemanticObject c, KnockdownChaseSpec spec)
    {
        var gate = c.Gate;
        if (gate is null || gate.Branches.Count == 0)
            return gate is null || gate.TriggerAll.Count == 0 ? "unknown" : Branch(gate.TriggerAll, [], null);
        var relations = gate.Branches.Select(b => Branch(gate.TriggerAll, b.Lines, b.Facets)).ToList();
        foreach (var strongest in new[] { "same-opportunity", "unknown", "command", "continuation", "disjoint", "human-only" })
            if (relations.Contains(strongest)) return strongest;
        return "unknown";
    }

    private static string Branch(IReadOnlyList<TriggerLine> all, IReadOnlyList<TriggerLine> lines, GateFacets? facets)
    {
        var exprs = all.Concat(lines).Select(l => l.Expression).ToList();
        if (exprs.Any(HumanOnly)) return "human-only";
        if (exprs.Any(e => ExprAnalyzer.Conjuncts(e).Any(NoControl))) return "disjoint";
        if (facets is not null)
        {
            if (facets.HasSourceStateConstraint && !NeutralStates.Any(n => StateRange.Any(facets.SourceStateRanges, n))) return "continuation";
            var types = facets.StateTypes.Where(t => !t.EndsWith('!')).Select(t => t.ToLowerInvariant()).ToList();
            if (types.Count > 0 && types.All(t => t == "a")) return "disjoint";
        }

        var conditions = exprs.SelectMany(BehaviorConditions.FromTrigger).Where(t => t.Required).Select(t => t.Condition).ToHashSet();
        if (conditions.Overlaps([Condition.EnemyAirborne, Condition.EnemyAttacking, Condition.EnemyBlocking, Condition.EnemyHasProjectile])) return "disjoint";
        if (conditions.Overlaps([Condition.EnemyLying, Condition.EnemyFalling, Condition.EnemyGettingUp])) return "same-opportunity";
        return facets is { Commands.Count: > 0 } ? "command" : "unknown";
    }

    /// <summary>Whether the rule can fire while the fighter is in the generated chase state (no control, standing, idle movetype).</summary>
    internal static bool StealsChase(SemanticIndex index, SemanticObject c, int chaseState, int depth = 0)
    {
        var gate = c.Gate;
        if (gate is null) return true;
        var branches = gate.Branches.Count > 0 ? gate.Branches.Select(b => (Lines: gate.TriggerAll.Concat(b.Lines).ToList(), Facets: (GateFacets?)b.Facets))
            : [(Lines: gate.TriggerAll.ToList(), Facets: (GateFacets?)null)];
        foreach (var (lines, facets) in branches)
        {
            var exprs = lines.Select(l => l.Expression).ToList();
            if (exprs.Any(HumanOnly)) continue;
            if (facets is null) return true;
            if (facets.CtrlRequired || exprs.Any(e => ExprAnalyzer.Conjuncts(e).Any(x => Control(x) || ControlOrStatesExcluding(x, chaseState)))) continue;
            // The chase state has no HitDef and never makes contact: a branch that needs contact (MoveContact / MoveHit / MoveGuarded) or an active HitDef
            // (HitDefAttr) cannot fire in it — the usual shape of a cancel ("trigger2 = hitdefattr = SC, NA … && movecontact").
            if (facets.Contact.Count > 0 || exprs.Any(e => ExprAnalyzer.Conjuncts(e).Any(NeedsHitDef))) continue;
            // A flag variable that cannot be set in the chase state (KFM's "combo condition" idiom, below).
            if (depth < 2 && exprs.SelectMany(ExprAnalyzer.Conjuncts).Any(x => FlagRead(x) is { } n && FlagFalseInChase(index, c, n, chaseState, depth))) continue;
            if (facets.HasSourceStateConstraint && !StateRange.Any(facets.SourceStateRanges, chaseState)) continue;
            if (StateRange.Any(facets.ExcludedStateRanges, chaseState)) continue;
            var types = facets.StateTypes.Where(t => !t.EndsWith('!')).Select(t => t.ToLowerInvariant()).ToList();
            if (types.Count > 0 && !types.Contains("s")) continue;
            var moves = facets.MoveTypes.Where(t => !t.EndsWith('!')).Select(t => t.ToLowerInvariant()).ToList();
            if (moves.Count > 0 && !moves.Contains("i")) continue;
            return true;
        }

        return false;
    }

    /// <summary><c>AILevel = 0</c>, <c>!AILevel</c> or <c>AILevel &lt; 1</c> as a required conjunct: the rule is for a human player only.</summary>
    private static bool HumanOnly(Expr e) => ExprAnalyzer.Conjuncts(e).Any(x => x switch
    {
        Unary { Op: "!", Operand: Ident { Name: "ailevel" } } => true,
        Binary { Op: "=", Left: Ident { Name: "ailevel" }, Right: NumberLit { Value: 0 } } => true,
        Binary { Op: "<", Left: Ident { Name: "ailevel" }, Right: NumberLit { Value: <= 1 } } => true,
        _ => false
    });

    /// <summary>A positive read of <c>var(N)</c> as a conjunct (<c>var(N)</c>, <c>var(N) = k≠0</c>, <c>var(N) &gt; 0</c>, <c>var(N) &gt;= k&gt;0</c>): N, else null.</summary>
    private static int? FlagRead(Expr e)
    {
        static int? Var(Expr x) => x is Call { Name: "var", Args: [NumberLit n] } ? (int)n.Value : null;
        return e switch
        {
            Call { Name: "var" } v => Var(v),
            Binary { Op: "=", Left: var l, Right: NumberLit { Value: not 0 } } => Var(l),
            Binary { Op: ">", Left: var l, Right: NumberLit { Value: >= 0 } } => Var(l),
            Binary { Op: ">=", Left: var l, Right: NumberLit { Value: > 0 } } => Var(l),
            _ => null
        };
    }

    /// <summary>
    /// One bounded idiom, not a solver: in State -1, an unconditional VarSet resets <c>var(N)</c> to 0 before <paramref name="reader"/>, and every VarSet /
    /// VarAdd that writes a non-zero value to it between that reset and the reader cannot itself fire in the chase state. Then <c>var(N)</c> is 0 whenever
    /// the reader is evaluated in the chase state (−3/−2 run before −1, the current state after it). Anything else is not proven (false).
    /// </summary>
    private static bool FlagFalseInChase(SemanticIndex index, SemanticObject reader, int n, int chaseState, int depth)
    {
        if (index.OwnerState(reader.Id)?.Id != "state:-1") return false;
        var row = index.ControllersOf("state:-1").ToList();
        var at = row.FindIndex(x => x.Id == reader.Id);
        var target = "var:var:" + n.ToString(CultureInfo.InvariantCulture);
        bool Writes(SemanticObject x, out string? value)
        {
            var w = index.Outgoing(x.Id, RelationKind.WritesVar).FirstOrDefault(r => r.To == target);
            value = w?.Prop("value");
            return w is not null;
        }

        var reset = -1;
        for (var i = at - 1; i >= 0; i--)
        {
            if (!Writes(row[i], out var v)) continue;
            if (v == "0" && Unconditional(row[i])) { reset = i; break; }
        }

        if (reset < 0) return false;
        for (var i = reset + 1; i < at; i++)
            if (Writes(row[i], out var v) && v != "0" && StealsChase(index, row[i], chaseState, depth + 1)) return false;
        return true;
    }

    /// <summary>A controller that fires every tick (<c>trigger1 = 1</c> and nothing else).</summary>
    private static bool Unconditional(SemanticObject c) =>
        c.Gate is { } g && g.TriggerAll.All(l => l.Expression is NumberLit { Value: not 0 }) && g.Branches.Count > 0 &&
        g.Branches.Any(b => b.Lines.All(l => l.Expression is NumberLit { Value: not 0 }));

    /// <summary>A conjunct that is only true while the player's own HitDef is active (<c>HitDefAttr = …</c>).</summary>
    private static bool NeedsHitDef(Expr e) => e switch
    {
        Binary { Op: "=", Left: Ident { Name: "hitdefattr" } or Call { Name: "hitdefattr" } } => true,
        Binary { Op: "=", Left: var l } when ExprPrinter.ToSExpr(l).Contains("hitdefattr", StringComparison.OrdinalIgnoreCase) => true,
        _ => false
    };

    private static bool NoControl(Expr e) => e switch
    {
        Unary { Op: "!", Operand: Ident { Name: "ctrl" } } => true,
        Binary { Op: "=", Left: Ident { Name: "ctrl" }, Right: NumberLit { Value: 0 } } => true,
        _ => false
    };

    private static bool Control(Expr e) => e switch
    {
        Ident { Name: "ctrl" } => true,
        Binary { Op: "=", Left: Ident { Name: "ctrl" }, Right: NumberLit { Value: 1 } } => true,
        _ => false
    };

    /// <summary>
    /// Another bounded idiom: <c>(ctrl || stateno = 21 || stateno = [500,501])</c> as a conjunct — control, or one of a few literal states. The generated
    /// chase runs with <c>ctrl = 0</c> in its own state, so the conjunct is false there unless one of those states covers the chase state. Any other
    /// disjunct (a variable, a range with computed bounds, <c>!=</c>) is not proven (false).
    /// </summary>
    private static bool ControlOrStatesExcluding(Expr e, int chaseState)
    {
        static IEnumerable<Expr> Disjuncts(Expr x) => x is Binary { Op: "||" } b ? Disjuncts(b.Left).Concat(Disjuncts(b.Right)) : [x];
        var control = false;
        foreach (var d in Disjuncts(e))
        {
            if (Control(d)) { control = true; continue; }
            (double Lo, double Hi)? states = d switch
            {
                Binary { Op: "=", Left: Ident { Name: "stateno" }, Right: NumberLit n } => (n.Value, n.Value),
                Binary { Op: "=", Left: Ident { Name: "stateno" }, Right: Interval { Low: NumberLit lo, High: NumberLit hi } } => (lo.Value, hi.Value),
                _ => null
            };
            if (states is not { } s || (chaseState >= s.Lo && chaseState <= s.Hi)) return false;
        }

        return control;
    }

    private static string Plain(SemanticIndex index, SemanticObject c, string hub, string relation, bool steals, string target, bool before, bool suppressed)
    {
        var to = target.StartsWith("state:", StringComparison.Ordinal) ? BehaviorText.State(index, int.TryParse(target["state:".Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null) : target;
        var what = relation switch
        {
            "same-opportunity" => "reads a knockdown itself",
            "command" => "is pressed by command — the engine's AI may press it",
            "unknown" => "decides on conditions X-Ray cannot read (variables, random…)",
            _ => "does not compete for the knockdown"
        };
        var order = hub == "state:-1" ? before ? "evaluated before the generated rule" : "evaluated after the generated rule" : $"in State {hub["state:".Length..]} (runs every tick, before State -1)";
        return $"{c.Name} → {to}: {what}; {order}" + (steals ? "; can change state during the chase" : string.Empty) + (suppressed ? "; suppressed on knockdowns" : string.Empty) + ".";
    }
}
