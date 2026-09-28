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

    /// <summary>User-facing failure message.</summary>
    public string? Error { get; set; }

    /// <summary>Raw technical cause (Win32 code and message) for diagnostics.</summary>
    public string? ErrorDetail { get; set; }

    public int? Win32Error { get; set; }
    public MutationFailureKind? FailureKind { get; set; }

    /// <summary>Hash the target must have after a ReplaceFile (the staged source's hash).</summary>
    public string? ExpectedAfterHash { get; set; }

    /// <summary>Attributes of the replaced file before the operation (restored on rollback).</summary>
    public string? OriginalAttributes { get; set; }

    /// <summary>"ReplaceFile", "MoveFileEx" or "Move" (target did not exist).</summary>
    public string? ReplaceMethod { get; set; }

    /// <summary>True when a ReadOnly attribute was lifted for the swap and re-applied afterwards.</summary>
    public bool ReadOnlyPreserved { get; set; }

    /// <summary>For failures: whether the target was verified to still hold its original bytes.</summary>
    public bool? OriginalVerifiedIntact { get; set; }

    public bool DryRun { get; set; }
    public IReadOnlyList<string> AffectedPaths { get; set; } = Array.Empty<string>();
}
