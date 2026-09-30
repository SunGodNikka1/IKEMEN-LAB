using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Model;

/// <summary>
/// How a fact is known. Every relationship and label carries one; UIs must render the three shipping levels differently.
/// </summary>
public enum Confidence
{
    /// <summary>The character's own files state it with literal operands. Says the code contains the edge, not that it is reachable.</summary>
    StaticProven,
    /// <summary>A heuristic, a non-literal step, or an author comment. Never equivalent to StaticProven.</summary>
    Inferred,
    /// <summary>Could not be determined (unparseable expression, dynamic target, missing reference).</summary>
    Unknown,
    /// <summary>Reserved for milestone 3 runtime evidence. Nothing in milestone 1 emits it.</summary>
    RuntimeVerified
}

public enum ObjectKind
{
    Character, File, Command, State, Controller, HitDef, Helper, Projectile, Variable,
    Animation, AnimFrame, Clsn, Sprite, Ability, Entity, Resource
}

public enum RelationKind
{
    Contains, ChangesState, SetsVictimState, SetsAttackerState, Binds, AffectsTarget,
    SpawnsHelper, HelperRunsState, SpawnsProjectile, ReferencesCommand,
    ReadsVar, WritesVar, ResetsVar, UsesAnim, AnimUsesSprite, HasClsn, DefinesHitDef,
    GatedByPower, ResourceCost, EntryPoint, PartOf, ActiveAtFrame
}

public sealed record Evidence(string RuleId, SourceRef? Source, string? Note);

/// <summary>An interpretation attached to an object (ability category, variable name, helper role).</summary>
public sealed record Label(string Text, string Category, Confidence Confidence, string RuleId);

/// <summary>One trigger line, kept both as text and as an AST so the Trigger Explorer can draw it.</summary>
public sealed record TriggerLine(string Text, Expr Expression, SourceRef? Source);

/// <summary>Facts pulled from one OR-branch (triggerall AND one triggerN group). Only literal syntax is used.</summary>
public sealed record GateFacets(
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> NegatedCommands,
    IReadOnlyList<string> Contact,
    IReadOnlyList<NumCompare> Power,
    IReadOnlyList<NumCompare> Time,
    IReadOnlyList<NumCompare> AnimElem,
    bool CtrlRequired,
    IReadOnlyList<string> StateTypes,
    IReadOnlyList<string> MoveTypes,
    bool ReadsAiLevel,
    int OtherConditions,
    IReadOnlyList<StateRange>? SourceStates = null,
    IReadOnlyList<StateRange>? ExcludedStates = null,
    IReadOnlyList<StateRange>? PrevStates = null,
    IReadOnlyList<TimeModFact>? TimeMods = null,
    IReadOnlyList<string>? Unmodelled = null)
{
    /// <summary>Union of state numbers the controller's own state must be in (<c>stateno = 200</c>, <c>= [200,210]</c>, <c>&gt;= 1000</c>); empty = not constrained.</summary>
    public IReadOnlyList<StateRange> SourceStateRanges => SourceStates ?? [];
    public IReadOnlyList<StateRange> ExcludedStateRanges => ExcludedStates ?? [];
    public IReadOnlyList<StateRange> PrevStateRanges => PrevStates ?? [];
    /// <summary>The conjuncts that are not a recognised shape (S-expression text); its count equals <see cref="OtherConditions"/>.</summary>
    public IReadOnlyList<string> UnmodelledConditions => Unmodelled ?? [];
    public bool HasSourceStateConstraint => SourceStates is { Count: > 0 };
}

/// <summary>Inclusive range of state numbers.</summary>
public readonly record struct StateRange(int Lo, int Hi)
{
    public bool Contains(int n) => n >= Lo && n <= Hi;
    public override string ToString() => Lo == Hi ? Lo.ToString() : Hi == int.MaxValue ? $"{Lo}+" : Lo == int.MinValue ? $"..{Hi}" : $"{Lo}-{Hi}";

    public static bool Any(IReadOnlyList<StateRange> ranges, int n)
    {
        foreach (var r in ranges) if (r.Contains(n)) return true;
        return false;
    }

    /// <summary>Intersection of two unions of ranges.</summary>
    public static List<StateRange> Intersect(IReadOnlyList<StateRange> a, IReadOnlyList<StateRange> b)
    {
        var result = new List<StateRange>();
        foreach (var x in a)
            foreach (var y in b)
            {
                var lo = Math.Max(x.Lo, y.Lo);
                var hi = Math.Min(x.Hi, y.Hi);
                if (lo <= hi) result.Add(new StateRange(lo, hi));
            }

        return result.OrderBy(r => r.Lo).ThenBy(r => r.Hi).ToList();
    }
}

public sealed record GateBranch(int Number, IReadOnlyList<TriggerLine> Lines, GateFacets Facets);

/// <summary>
/// The trigger structure of a controller: it fires when every TriggerAll line holds and at least one branch holds.
/// <see cref="Facets"/> per branch already include the TriggerAll conjuncts.
/// </summary>
public sealed record Gate(IReadOnlyList<TriggerLine> TriggerAll, IReadOnlyList<GateBranch> Branches)
{
    public bool IsEmpty => TriggerAll.Count == 0 && Branches.Count == 0;
}

public sealed class SemanticObject
{
    public required string Id { get; init; }
    public required ObjectKind Kind { get; init; }
    public required string Name { get; set; }
    public SourceRef? Source { get; set; }
    public string? ParentId { get; set; }
    public SortedDictionary<string, string> Props { get; } = new(StringComparer.Ordinal);
    public List<Label> Labels { get; } = [];
    public Gate? Gate { get; set; }
    public bool IsStub => Props.ContainsKey("unresolved");

    public string? Prop(string key) => Props.TryGetValue(key, out var v) ? v : null;
}

public sealed class Relationship
{
    public required string Id { get; init; }
    public required RelationKind Kind { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required Confidence Confidence { get; init; }
    public List<Evidence> Evidence { get; } = [];
    public SortedDictionary<string, string> Props { get; } = new(StringComparer.Ordinal);
    /// <summary>The gate that must hold for this edge (ChangesState, Spawns*): the controller's own gate.</summary>
    public Gate? Gate { get; init; }

    public string? Prop(string key) => Props.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Label categories. Labels are interpretations, always Inferred; see <see cref="EvidenceRules"/>.</summary>
public static class LabelCategories
{
    public const string AbilityCategory = "ability-category";
    public const string HelperRole = "helper-role";
    public const string VariableName = "variable-name";
    public const string VariableTrait = "variable-trait";
    /// <summary>Author-supplied name (comment above a block).</summary>
    public const string Name = "name";
    public const string Hub = "hub";
}
