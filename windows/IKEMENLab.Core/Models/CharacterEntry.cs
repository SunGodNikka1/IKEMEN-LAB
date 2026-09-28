using IKEMENLab.Core.Library;

namespace IKEMENLab.Core.Models;

public sealed record CharacterEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string VersionDate { get; init; }
    public required string DefPath { get; init; }
    public required string FolderPath { get; init; }
    public string? SpriteFile { get; init; }
    public bool Nested { get; init; }

    /// <summary>select.def registration (Unregistered until a select.def index is applied).</summary>
    public ContentStatus Status { get; init; } = ContentStatus.Unregistered;

    /// <summary>When this character was added to the installation (see <see cref="DateAddedTracker"/>).</summary>
    public DateTime? DateAddedUtc { get; init; }

    /// <summary>How <see cref="DateAddedUtc"/> was established (exact install, first seen, legacy estimate).</summary>
    public DateAddedSource? DateAddedSource { get; init; }

    /// <summary>Last write time of the character DEF.</summary>
    public DateTime? ModifiedAtUtc { get; init; }
}
