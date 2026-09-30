using IKEMENLab.Core.Library;
using IKEMENLab.Core.Mutations;

namespace IKEMENLab.Core.Install;

public enum InstallContentKind
{
    Character,
    Stage,
    Screenpack
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

    /// <summary>Stages: files the DEF references outside the stage folder that install with it.</summary>
    public IReadOnlyList<StageCompanion> Companions { get; init; } = Array.Empty<StageCompanion>();

    /// <summary>Stages: true when the stage installs as loose files directly under stages/.</summary>
    public bool IsFlatStage { get; init; }

    /// <summary>Characters: where <see cref="SuggestedFolderName"/> came from (never a DEF file name).</summary>
    public FolderNameSource NameSource { get; init; } = FolderNameSource.Detected;

    /// <summary>Characters: every character DEF in the package, with the facts used to pick one.</summary>
    public IReadOnlyList<DefCandidate> DefCandidates { get; init; } = Array.Empty<DefCandidate>();

    /// <summary>Characters: the resolver's primary DEF (null when the choice is ambiguous).</summary>
    public DefCandidate? PrimaryDef { get; init; }

    public PrimaryDefRule PrimaryRule { get; init; } = PrimaryDefRule.OnlyCandidate;

    /// <summary>Packaging folders above the character folder that are not installed.</summary>
    public IReadOnlyList<string> Wrappers { get; init; } = Array.Empty<string>();

    /// <summary>Documentation outside the character folder that is not installed.</summary>
    public IReadOnlyList<string> LeftOutDocs { get; init; } = Array.Empty<string>();

    /// <summary>Other files outside the character folder; installing without them must be confirmed.</summary>
    public IReadOnlyList<string> LeftOutFiles { get; init; } = Array.Empty<string>();

    public bool HasMissingAssets => MissingAssets.Count > 0;

    public bool RequiresDefChoice => PrimaryRule == PrimaryDefRule.Ambiguous;
    public bool RequiresLayoutConfirmation => LeftOutFiles.Count > 0;
}

public sealed class InstallPlanItem
{
    public required DetectedPackage Package { get; init; }
    /// <summary>Destination folder; changed only through <see cref="InstallPlanRules.Rename"/>.</summary>
    public required string TargetDirectory { get; set; }

    public required bool DestinationExists { get; set; }
    public InstallItemDecision Decision { get; set; }
    public InstallItemOutcome Outcome { get; set; } = InstallItemOutcome.Pending;
    public string? Error { get; set; }
    public OperationPlan? MutationPlan { get; set; }
    public string? OperationId { get; set; }
    public IReadOnlyList<string> AffectedPaths { get; init; } = Array.Empty<string>();

    /// <summary>Folder name under chars/ (or data/, stages/) this item installs to.</summary>
    public string DestinationName =>
        Path.GetFileName(TargetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>
    /// The DEF the user picked, relative to the package folder. Null means the resolver's primary;
    /// an ambiguous package cannot be installed until one is picked.
    /// </summary>
    public string? SelectedDef { get; set; }

    /// <summary>The user accepted installing without the loose files outside the character folder.</summary>
    public bool LayoutConfirmed { get; set; }

    /// <summary>Why this item cannot be installed as planned (invalid name, same destination twice).</summary>
    public string? Problem { get; set; }

    /// <summary>Warnings about replacing the existing folder (e.g. a DEF select.def uses would disappear).</summary>
    public IReadOnlyList<string> ReplaceWarnings { get; set; } = Array.Empty<string>();

    public bool NeedsDefChoice => Package.RequiresDefChoice && SelectedDef is null;
    public bool NeedsLayoutConfirmation => Package.RequiresLayoutConfirmation && !LayoutConfirmed;

    /// <summary>The DEF this install will use, relative to the package folder.</summary>
    public string? EffectiveDef => SelectedDef ?? Package.PrimaryDef?.RelativePath;

    public string KindLabel => Package.Kind switch
    {
        InstallContentKind.Character => "Character",
        InstallContentKind.Stage => "Stage",
        InstallContentKind.Screenpack => "Screenpack",
        _ => Package.Kind.ToString()
    };
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
