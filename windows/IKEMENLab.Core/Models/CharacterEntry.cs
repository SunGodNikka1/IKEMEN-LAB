using IKEMENLab.Core.Library;

namespace IKEMENLab.Core.Models;

public sealed record CharacterEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string VersionDate { get; init; }
    /// <summary>
    /// Root-relative primary DEF (see <see cref="PrimaryDefResolver"/>). When <see cref="NeedsDefChoice"/>
    /// is set this only labels the character; it is never enabled without the user's choice.
    /// </summary>
    public required string DefPath { get; init; }
    public required string FolderPath { get; init; }
    public string? SpriteFile { get; init; }
    public bool Nested { get; init; }

    /// <summary>
    /// select.def registration of the character folder: Active when any of its DEFs is listed
    /// uncommented (Unregistered until a select.def index is applied).
    /// </summary>
    public ContentStatus Status { get; init; } = ContentStatus.Unregistered;

    /// <summary>Root-relative character DEFs in the folder (the primary plus alternatives).</summary>
    public IReadOnlyList<string> DefCandidates { get; init; } = [];

    /// <summary>Which rule chose <see cref="DefPath"/>.</summary>
    public PrimaryDefRule PrimaryRule { get; init; } = PrimaryDefRule.OnlyCandidate;

    /// <summary>Several plausible DEFs and nothing decides: enabling must ask which one.</summary>
    public bool NeedsDefChoice { get; init; }

    /// <summary>Root-relative DEFs of this folder that select.def lists uncommented.</summary>
    public IReadOnlyList<string> ActiveDefPaths { get; init; } = [];

    /// <summary>Root-relative DEFs of this folder that select.def lists only commented out.</summary>
    public IReadOnlyList<string> DisabledDefPaths { get; init; } = [];

    /// <summary>When this character was added to the installation (see <see cref="DateAddedTracker"/>).</summary>
    public DateTime? DateAddedUtc { get; init; }

    /// <summary>How <see cref="DateAddedUtc"/> was established (exact install, first seen, legacy estimate).</summary>
    public DateAddedSource? DateAddedSource { get; init; }

    /// <summary>Last write time of the character DEF.</summary>
    public DateTime? ModifiedAtUtc { get; init; }
}
