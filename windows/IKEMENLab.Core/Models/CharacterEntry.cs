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

    /// <summary>Creation time of the top-level folder under chars/ (≈ install time on Windows).</summary>
    public DateTime? InstalledAtUtc { get; init; }

    /// <summary>Last write time of the character DEF.</summary>
    public DateTime? ModifiedAtUtc { get; init; }
}
