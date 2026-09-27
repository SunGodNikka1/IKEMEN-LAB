namespace IKEMENLab.Core.Models;

public sealed class LibrarySnapshot
{
    public required InstallationCheck Installation { get; init; }
    public required IReadOnlyList<CharacterEntry> Characters { get; init; }
    public required IReadOnlyList<StageEntry> Stages { get; init; }
    public required IReadOnlyList<IndexWarning> Warnings { get; init; }
    public DateTimeOffset IndexedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CharacterCount => Characters.Count;
    public int StageCount => Stages.Count;
    public int NestedCharacterCount => Characters.Count(c => c.Nested);
    public int TopLevelCharacterCount => Characters.Count(c => !c.Nested);
}
