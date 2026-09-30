using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Combo;

/// <summary>What the gate says must have happened to the source move. Ordered weakest → strongest evidence of a hit.</summary>
public enum ContactRequirement { None, Guarded, Contact, Hit }

public enum EdgeKind
{
    /// <summary>Neutral → move: a command starts a move.</summary>
    Start,
    /// <summary>Move → move driven by an input or by contact (movehit / movecontact / moveguarded): the classic cancel. With no contact condition it also fires on whiff.</summary>
    Cancel,
    /// <summary>HitDef p1stateno: the attacker enters this state when the hit lands.</summary>
    OnHit,
    /// <summary>Move → move with no input and no contact condition: the move continuing by itself (a timer or animation frame).</summary>
    Chain,
    /// <summary>Move → move that needs ctrl: only possible after recovery, i.e. a link.</summary>
    Link,
    /// <summary>Move → neutral.</summary>
    Recovery
}

/// <summary>One rule that backs an edge, pointing at the relationship of the semantic index it was derived from.</summary>
public sealed record EdgeEvidence(string RuleId, string? RelationshipId, SourceRef? Source);

/// <summary>What is known about a state as a move: its hit, cost and timing. Values are literal-only; anything else is null.</summary>
public sealed record MoveInfo(
    string StateId, int Number, string Name, string? StateType, string? MoveType, bool IsCommon, bool IsNeutral,
    int HitDefCount, double? Damage, bool DamageExact, int? PauseTime, string? HitFlag, string? GuardFlag, string? GroundType,
    string? AnimId, int? AnimTicks, int? FirstHitTick, double PowerCost, IReadOnlyList<string> Abilities);

/// <summary>
/// A transition of the state graph read as a combo candidate. It keeps the gate facets and the conditions the index could not
/// model, and the confidence of the weakest fact it depends on. It says "this input could connect these states", never "this works".
/// </summary>
public sealed class CandidateEdge
{
    public required string Id { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required EdgeKind Kind { get; init; }
    public required string ControllerId { get; init; }
    public required string RelationshipId { get; init; }
    public required int BranchNumber { get; init; }
    public required ContactRequirement Contact { get; init; }
    public required Confidence Confidence { get; init; }
    public required GateFacets Facets { get; init; }
    public required IReadOnlyList<EdgeEvidence> Evidence { get; init; }
    /// <summary>Conditions this edge depends on that are not modelled (unrecognised trigger lines plus structural caveats).</summary>
    public required IReadOnlyList<string> Unmodelled { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>The edge comes from a -1/-2/-3 controller and was expanded to this source state.</summary>
    public bool IsGlobal { get; init; }
    public bool SourceHasHitDef { get; init; }
    /// <summary>Earliest tick of the source state at which the gate can hold, when Time/AnimElem gates say so.</summary>
    public int? EarliestTick { get; init; }

    public IReadOnlyList<string> Commands => Facets.Commands;
    public bool IsHitConfirm => Contact is ContactRequirement.Hit or ContactRequirement.Contact;
}

public sealed record ReadinessReport(
    int States, int MoveStates, int NeutralStates, int Edges, IReadOnlyDictionary<string, int> EdgesByKind,
    IReadOnlyDictionary<string, int> EdgesByConfidence, int EdgesWithUnmodelled, int DynamicTargets, int MovesWithHitDef,
    int MovesWithLiteralDamage, int MovesWithKnownTicks, int GlobalEdges, int UnconstrainedGlobalEdges, IReadOnlyList<string> Findings);

public enum ComboStrategy { Dfs, Beam, BestFirst }

public sealed record ComboOptions
{
    /// <summary>Start in this state (as if it just connected). Null starts from neutral with a command.</summary>
    public string? From { get; init; }
    /// <summary>Most move states in a route.</summary>
    public int MaxMoves { get; init; } = 4;
    public int StartMeter { get; init; }
    public int? MaxFrames { get; init; }
    /// <summary>Cancels must need movehit/movecontact; guard-only and whiff cancels are dropped. On-hit states and chains are governed by their own rules.</summary>
    public bool HitConfirmOnly { get; init; } = true;
    /// <summary>Also follow the move continuing by itself (Chain edges); on by default because multi-stage specials rely on it.</summary>
    public bool AllowChains { get; init; } = true;
    /// <summary>Also follow Link/Recovery edges through neutral. Off by default: timing is not modelled.</summary>
    public bool AllowLinks { get; init; }
    public int MaxRepeats { get; init; } = 1;
    public int Top { get; init; } = 20;
    public ComboStrategy Strategy { get; init; } = ComboStrategy.Dfs;
    public int BeamWidth { get; init; } = 200;
    public int MaxExpansions { get; init; } = 200_000;
    /// <summary>Drop edges with more than this many unmodelled conditions (null = keep all, and list them).</summary>
    public int? MaxUnmodelledPerEdge { get; init; }
    /// <summary>Weakest confidence an edge may have. Unknown edges never enter the graph, so Inferred is the practical floor.</summary>
    public Confidence WeakestAllowed { get; init; } = Confidence.Inferred;
}

public sealed record ComboStep(CandidateEdge Edge, MoveInfo Move, double? Damage, int MeterAfter);

/// <summary>A route through the candidate graph. Always a candidate: <see cref="Confidence"/> is the weakest edge, never runtime-verified.</summary>
public sealed class ComboRoute
{
    /// <summary>The state the route begins in ("neutral" when it starts from a command).</summary>
    public required string StartState { get; init; }
    public required IReadOnlyList<ComboStep> Steps { get; init; }
    public required double DamageKnown { get; init; }
    public required bool DamageComplete { get; init; }
    public required int MeterSpent { get; init; }
    public required int EndMeter { get; init; }
    public int? MinFrames { get; init; }
    public required bool FramesComplete { get; init; }
    public required Confidence Confidence { get; init; }
    public required int UnmodelledCount { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public string Key => string.Join(">", Steps.Select(s => s.Edge.Id));
    public IEnumerable<string> States => Steps.Select(s => s.Move.StateId);
}

public sealed class ComboSearchResult
{
    public required ComboOptions Options { get; init; }
    public required IReadOnlyList<ComboRoute> Routes { get; init; }
    public required int Expansions { get; init; }
    public required bool Truncated { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}
