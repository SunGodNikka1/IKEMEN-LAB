using IKEMENLab.Core.Models;

namespace IKEMENLab.Core.XRay.Workbench;

/// <summary>Finds the IKEMEN root and character DEF for a folder or DEF path. The CLI and the MCP server resolve characters through it.</summary>
public static class CharacterLocator
{
    public sealed record Located(string Root, CharacterEntry Entry);

    public static Located? Locate(string target, string? explicitRoot)
    {
        var full = Path.GetFullPath(target);
        string? def = null;
        if (File.Exists(full) && full.EndsWith(".def", StringComparison.OrdinalIgnoreCase)) def = full;
        else if (Directory.Exists(full))
        {
            var folder = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var named = Path.Combine(full, folder + ".def");
            def = File.Exists(named) ? named : Directory.EnumerateFiles(full, "*.def").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(p => File.ReadAllText(p).Contains("[Files]", StringComparison.OrdinalIgnoreCase));
        }

        if (def is null) return null;
        var charDir = Path.GetDirectoryName(def)!;
        var root = explicitRoot is not null ? Path.GetFullPath(explicitRoot) : FindRoot(charDir);
        var rel = Path.GetRelativePath(root, def).Replace('\\', '/');
        var folderRel = Path.GetRelativePath(root, charDir).Replace('\\', '/');
        var id = folderRel.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? folderRel["chars/".Length..] : Path.GetFileName(charDir);
        var parsed = IKEMENLab.Core.Parsing.DefFileReader.ReadFileContent(def) is { } text ? IKEMENLab.Core.Parsing.DefParser.Parse(text) : null;
        var name = parsed?.Value("displayname", "info") ?? parsed?.Value("name", "info") ?? Path.GetFileNameWithoutExtension(def);
        return new Located(root, new CharacterEntry
        {
            Id = id, DisplayName = name, Name = parsed?.Value("name", "info") ?? name,
            Author = parsed?.Value("author", "info") ?? string.Empty, VersionDate = string.Empty, DefPath = rel, FolderPath = folderRel
        });
    }

    private static string FindRoot(string charDir)
    {
        for (var dir = new DirectoryInfo(charDir); dir?.Parent is not null; dir = dir.Parent)
            if (dir.Parent.Name.Equals("chars", StringComparison.OrdinalIgnoreCase) && dir.Parent.Parent is { } root)
                return root.FullName;
        return Path.GetDirectoryName(charDir) ?? charDir;
    }
}
