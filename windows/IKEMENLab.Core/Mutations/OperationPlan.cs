namespace IKEMENLab.Core.Mutations;

/// <summary>
/// Preview of a mutation before execution. Dry-run returns this without touching the IKEMEN root.
/// </summary>
public sealed class OperationPlan
{
    public required MutationKind Kind { get; init; }
    public required string IkemenRoot { get; init; }
    public required string TargetPath { get; init; }
    public string? SourcePath { get; init; }
    public bool WouldOverwrite { get; init; }
    public bool BackupRequired { get; init; }
    public bool IsValid { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<string> AffectedPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
