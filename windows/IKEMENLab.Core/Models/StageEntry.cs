namespace IKEMENLab.Core.Models;

public sealed record StageEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }

    /// <summary>DEF path relative to stages/ with forward slashes (e.g. "Pack/fancy.def").</summary>
    public required string DefPath { get; init; }

    /// <summary>select.def [ExtraStages] registration.</summary>
    public ContentStatus Status { get; init; } = ContentStatus.Unregistered;

    /// <summary>Creation time of the stage DEF (≈ install time on Windows).</summary>
    public DateTime? InstalledAtUtc { get; init; }

    /// <summary>Last write time of the stage DEF.</summary>
    public DateTime? ModifiedAtUtc { get; init; }

    /// <summary>Root-relative DEF path as select.def references it (e.g. "stages/Pack/fancy.def").</summary>
    public string RootRelativeDefPath => "stages/" + DefPath;
}
