using IKEMENLab.Core.Mutations;

namespace IKEMENLab.Core.Install;

public enum InstallContentKind
{
    Character,
    Stage
}

public enum InstallItemDecision
{
    /// <summary>New install (destination does not exist).</summary>
    InstallNew,
    /// <summary>Destination exists; user must choose Replace or Skip.</summary>
    NeedsDecision,
    Replace,
    Skip
}

public enum InstallItemOutcome
{
    Pending,
    Installed,
    Skipped,
    Failed,
    Rejected
}

/// <summary>Detected package ready for preview / mutation planning.</summary>
public sealed class DetectedPackage
{
    public required InstallContentKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string SuggestedFolderName { get; init; }
    public required string PackageRoot { get; init; }
    public required string DefPath { get; init; }
    public required string SourceInput { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingAssets { get; init; } = Array.Empty<string>();
    public int FileCount { get; init; }
}

public sealed class InstallPlanItem
{
    public required DetectedPackage Package { get; init; }
    public required string TargetDirectory { get; init; }
    public required bool DestinationExists { get; init; }
    public InstallItemDecision Decision { get; set; }
    public InstallItemOutcome Outcome { get; set; } = InstallItemOutcome.Pending;
    public string? Error { get; set; }
    public OperationPlan? MutationPlan { get; set; }
    public string? OperationId { get; set; }
    public IReadOnlyList<string> AffectedPaths { get; init; } = Array.Empty<string>();

    public string KindLabel => Package.Kind == InstallContentKind.Character ? "Character" : "Stage";
    public string StatusLabel => Decision switch
    {
        InstallItemDecision.InstallNew => "New",
        InstallItemDecision.Replace => "Replace",
        InstallItemDecision.Skip => "Skip",
        InstallItemDecision.NeedsDecision => "Exists — choose Replace or Skip",
        _ => Decision.ToString()
    };
}

public sealed class InspectBatchResult
{
    public required IReadOnlyList<InstallPlanItem> Items { get; init; }
    public required IReadOnlyList<InspectFailure> Failures { get; init; }
    /// <summary>Staging directories created for archives; caller should dispose/cleanup after install or cancel.</summary>
    public required IReadOnlyList<string> StagingDirectories { get; init; }
}

public sealed class InspectFailure
{
    public required string SourceInput { get; init; }
    public required string Reason { get; init; }
}

public sealed class InstallBatchResult
{
    public required IReadOnlyList<InstallPlanItem> Items { get; init; }
    public int InstalledCount => Items.Count(i => i.Outcome == InstallItemOutcome.Installed);
    public int SkippedCount => Items.Count(i => i.Outcome == InstallItemOutcome.Skipped);
    public int FailedCount => Items.Count(i => i.Outcome is InstallItemOutcome.Failed or InstallItemOutcome.Rejected);
}
