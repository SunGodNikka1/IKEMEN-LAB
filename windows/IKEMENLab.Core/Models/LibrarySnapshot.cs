using IKEMENLab.Core.Config;
using IKEMENLab.Core.Screenpacks;
using IKEMENLab.Core.SelectDef;

namespace IKEMENLab.Core.Models;

public sealed class LibrarySnapshot
{
    public required InstallationCheck Installation { get; init; }
    public required IReadOnlyList<CharacterEntry> Characters { get; init; }
    public required IReadOnlyList<StageEntry> Stages { get; init; }
    public required IReadOnlyList<IndexWarning> Warnings { get; init; }
    public DateTimeOffset IndexedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>IKEMEN configuration as read (never written) from save/config.ini.</summary>
    public IkemenConfig Config { get; init; } = IkemenConfig.Missing;

    /// <summary>Motifs under data/ (read-only), active one first.</summary>
    public IReadOnlyList<ScreenpackEntry> Screenpacks { get; init; } = Array.Empty<ScreenpackEntry>();

    /// <summary>Active select.def (read-only) used to derive registration status.</summary>
    public SelectDefIndex? SelectDef { get; init; }

    public int CharacterCount => Characters.Count;
    public int StageCount => Stages.Count;
    public int NestedCharacterCount => Characters.Count(c => c.Nested);
    public int TopLevelCharacterCount => Characters.Count(c => !c.Nested);
    public int ActiveCharacterCount => Characters.Count(c => c.Status == ContentStatus.Active);
    public int ActiveStageCount => Stages.Count(s => s.Status == ContentStatus.Active);
}
