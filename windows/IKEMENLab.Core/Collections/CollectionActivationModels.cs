namespace IKEMENLab.Core.Collections;

public enum CollectionRosterStatus
{
    /// <summary>No remembered activation, or remembered collection no longer exists.</summary>
    NotActive,
    /// <summary>select.def Active set exactly matches the collection's resolved membership.</summary>
    Active,
    /// <summary>A collection was activated earlier, but select.def no longer matches.</summary>
    Modified,
    /// <summary>Cannot activate (no select.def, ambiguity, etc.).</summary>
    CannotActivate
}

public sealed class CollectionActivationPreview
{
    public required string CollectionName { get; init; }
    public Guid? CollectionId { get; init; }
    public bool IsAllCharacters { get; init; }
    public int TotalResolvedMembers { get; init; }
    public IReadOnlyList<string> WillEnable { get; init; } = [];
    public IReadOnlyList<string> WillDisable { get; init; } = [];
    public IReadOnlyList<string> AlreadyActive { get; init; } = [];
    public IReadOnlyList<string> Missing { get; init; } = [];
    public IReadOnlyList<string> Ambiguous { get; init; } = [];
    public IReadOnlyList<string> WillEnableNames { get; init; } = [];
    public IReadOnlyList<string> WillDisableNames { get; init; } = [];
    public IReadOnlyList<string> AlreadyActiveNames { get; init; } = [];
    public IReadOnlyList<string> MissingNames { get; init; } = [];
    public IReadOnlyList<string> AmbiguousNames { get; init; } = [];
    public bool CanActivate { get; init; }
    public string? Error { get; init; }
    public string? Warning { get; init; }
    public string? SelectDefPath { get; init; }

    public string Summary =>
        $"Will Enable: {WillEnable.Count}  ·  Will Disable: {WillDisable.Count}  ·  Already Active: {AlreadyActive.Count}  ·  Missing: {Missing.Count}  ·  Ambiguous: {Ambiguous.Count}";
}

public sealed class CollectionActivationResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? Description { get; init; }
    public string? Warning { get; init; }
    public string? SelectDefPath { get; init; }
    public string? OperationId { get; init; }
    public bool Changed { get; init; }
    public CollectionActivationPreview? Preview { get; init; }
    public Mutations.OperationPlan? Plan { get; init; }
}
