using IKEMENLab.Core.Models;

namespace IKEMENLab.App.ViewModels;

public sealed class CharacterCardViewModel
{
    public CharacterCardViewModel(CharacterEntry entry)
    {
        Entry = entry;
        Initials = MakeInitials(entry.DisplayName);
    }

    public CharacterEntry Entry { get; }
    public string DisplayName => Entry.DisplayName;
    public string Author => Entry.Author;
    public string FolderId => Entry.Id;
    public string DefPath => Entry.DefPath;
    public string Initials { get; }
    public string NestedLabel => Entry.Nested ? "Nested" : "Top-level";

    private static string MakeInitials(string name)
    {
        var parts = name.Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant();
    }
}
