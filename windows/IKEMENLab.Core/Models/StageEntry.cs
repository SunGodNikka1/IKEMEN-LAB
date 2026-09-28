using IKEMENLab.Core.Library;

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

    /// <summary>When this stage was added to the installation (see <see cref="DateAddedTracker"/>).</summary>
    public DateTime? DateAddedUtc { get; init; }

    /// <summary>How <see cref="DateAddedUtc"/> was established (exact install, first seen, legacy estimate).</summary>
    public DateAddedSource? DateAddedSource { get; init; }

    /// <summary>Last write time of the stage DEF.</summary>
    public DateTime? ModifiedAtUtc { get; init; }

    /// <summary>Root-relative DEF path as select.def references it (e.g. "stages/Pack/fancy.def").</summary>
    public string RootRelativeDefPath => "stages/" + DefPath;

    /// <summary>[Music] bgmusic/bgm value as written, if any.</summary>
    public string? BgmReference { get; init; }

    /// <summary>True when the referenced BGM file exists (IKEMEN search: DEF dir, root, data/, sound/).</summary>
    public bool BgmFound { get; init; }

    public bool HasBgm => !string.IsNullOrWhiteSpace(BgmReference);

    /// <summary>[Camera] boundleft/boundright (null when absent).</summary>
    public int? BoundLeft { get; init; }
    public int? BoundRight { get; init; }
}
