using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Services;

/// <summary>
/// Read-only character discovery with one-level nested DEF fallback.
/// Never writes beneath the IKEMEN root.
/// </summary>
public sealed class CharacterIndexer
{
    public IReadOnlyList<CharacterEntry> Index(string rootPath, out IReadOnlyList<IndexWarning> warnings)
    {
        var warningList = new List<IndexWarning>();
        var characters = new List<CharacterEntry>();
        var charsRoot = Path.Combine(rootPath, "chars");

        if (!Directory.Exists(charsRoot))
        {
            warnings = warningList;
            return characters;
        }

        foreach (var topFolder in Directory.EnumerateDirectories(charsRoot))
        {
            if (IkemenLabStaging.IsStagingName(topFolder)) continue;
            try
            {
                var entry = ResolveCharacter(rootPath, charsRoot, topFolder, nested: false);
                if (entry is null)
                {
                    entry = TryNestedFallback(rootPath, charsRoot, topFolder);
                }


                if (entry is null)
                {
                    warningList.Add(new IndexWarning
                    {
                        Path = Relativize(charsRoot, topFolder),
                        Message = "No valid character DEF found (top-level or one nested level)."
                    });
                    continue;
                }

                characters.Add(entry);
            }
            catch (Exception ex)
            {
                warningList.Add(new IndexWarning
                {
                    Path = Relativize(charsRoot, topFolder),
                    Message = $"Skipped due to error: {ex.Message}"
                });
            }
        }

        characters.Sort(static (a, b) =>
        {
            var byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        warnings = warningList;
        return characters;
    }

    private static CharacterEntry? TryNestedFallback(string rootPath, string charsRoot, string topFolder)
    {
        foreach (var nestedFolder in Directory.EnumerateDirectories(topFolder)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var entry = ResolveCharacter(rootPath, charsRoot, nestedFolder, nested: true);
            if (entry is not null) return entry;
        }

        return null;
    }

    private static CharacterEntry? ResolveCharacter(string rootPath, string charsRoot, string folder, bool nested)
    {
        var folderName = Path.GetFileName(folder);
        var preferredPath = Path.Combine(folder, folderName + ".def");
        string? defPath = null;

        if (File.Exists(preferredPath) && DefContentClassifier.IsValidCharacterDefFile(preferredPath))
        {
            defPath = preferredPath;
        }
        else
        {
            var candidates = Directory.EnumerateFiles(folder, "*.def")
                .Where(DefContentClassifier.IsValidCharacterDefFile)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            defPath = candidates.FirstOrDefault(f =>
                string.Equals(Path.GetFileNameWithoutExtension(f), folderName, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault();
        }

        if (defPath is null) return null;

        DefParseResult? parsed;
        try
        {
            parsed = DefParser.ParseFile(defPath);
        }
        catch
        {
            return null;
        }

        var relativeFolder = Relativize(charsRoot, folder).Replace('\\', '/');
        var parsedName = parsed?.Name;
        if (string.IsNullOrWhiteSpace(parsedName))
        {
            parsedName = Path.GetFileName(folder);
        }

        var displayName = parsedName;
        var displayFallback = parsed?.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(displayFallback))
        {
            displayName = displayFallback!;
        }

        return new CharacterEntry
        {
            Id = relativeFolder,
            Name = parsedName!,
            DisplayName = displayName!,
            Author = string.IsNullOrWhiteSpace(parsed?.Author) ? "Unknown" : parsed!.Author!,
            VersionDate = parsed?.VersionDate ?? string.Empty,
            DefPath = Relativize(rootPath, defPath).Replace('\\', '/'),
            FolderPath = relativeFolder,
            SpriteFile = parsed?.SpriteFile,
            Nested = nested,
            ModifiedAtUtc = SafeLastWrite(defPath)
        };
    }

    private static DateTime? SafeLastWrite(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Relativize(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Replace('\\', '/');
    }
}
