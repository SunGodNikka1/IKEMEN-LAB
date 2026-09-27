namespace IKEMENLab.Core.Models;

public sealed class CharacterEntry
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
}
