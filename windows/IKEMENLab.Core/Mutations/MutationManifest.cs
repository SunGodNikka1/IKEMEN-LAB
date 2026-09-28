namespace IKEMENLab.Core.Mutations;

/// <summary>
/// JSON-serializable record of one mutation attempt. Stored under LocalAppData, never under the IKEMEN root.
/// </summary>
public sealed class MutationManifest
{
    public required string OperationId { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public required MutationKind Kind { get; set; }
    public required string IkemenRoot { get; set; }
    public required string TargetPath { get; set; }
    public string? SourcePath { get; set; }
    public string? BackupPath { get; set; }
    public string? StagingPath { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public required MutationStatus Status { get; set; }
    public string? Error { get; set; }
    public bool DryRun { get; set; }
    public IReadOnlyList<string> AffectedPaths { get; set; } = Array.Empty<string>();
}
