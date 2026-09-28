using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.SelectDef;

namespace IKEMENLab.Core.Services;

/// <summary>What the library knows beyond the files when choosing a character's primary DEF.</summary>
public sealed class CharacterIndexContext
{
    /// <summary>select.def references below a root-relative folder ("chars/Muzan").</summary>
    public Func<string, RosterReferences>? Roster { get; init; }

    /// <summary>Saved choices: character identity → DEF relative to the character folder.</summary>
    public IReadOnlyDictionary<string, string>? SavedChoices { get; init; }
}

/// <summary>
/// Read-only character discovery: one entry per folder under chars/, with the primary DEF chosen by
/// <see cref="PrimaryDefResolver"/> (DEFs at the top of the folder, or one nested level down).
/// Never writes beneath the IKEMEN root.
/// </summary>
public sealed class CharacterIndexer
{
    public IReadOnlyList<CharacterEntry> Index(string rootPath, out IReadOnlyList<IndexWarning> warnings)
        => Index(rootPath, out warnings, context: null);

    public IReadOnlyList<CharacterEntry> Index(string rootPath, out IReadOnlyList<IndexWarning> warnings, CharacterIndexContext? context)
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
                var entry = ResolveCharacter(rootPath, charsRoot, topFolder, context);
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

    private static CharacterEntry? ResolveCharacter(string rootPath, string charsRoot, string topFolder, CharacterIndexContext? context)
    {
        var topName = Path.GetFileName(topFolder);
        var folderPrefix = "chars/" + topName + "/";
        var roster = context?.Roster?.Invoke("chars/" + topName) ?? RosterReferences.None;
        string? saved = null;
        context?.SavedChoices?.TryGetValue(ContentIdentity.ForCharacterFolder(topName), out saved);

        string InFolder(string rootRelative) => rootRelative.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)
            ? rootRelative[folderPrefix.Length..]
            : rootRelative;

        var decision = PrimaryDefResolver.Resolve(topFolder, topName, new PrimaryDefHints
        {
            SavedChoice = saved,
            ActiveRoster = roster.Active.Select(InFolder).ToList(),
            DisabledRoster = roster.Disabled.Select(InFolder).ToList(),
            SearchRoots = [rootPath, Path.Combine(rootPath, "data")]
        });

        var chosen = decision.DisplayCandidate;
        if (chosen is null) return null;

        DefParseResult? parsed;
        try
        {
            parsed = DefParser.ParseFile(chosen.FullPath);
        }
        catch
        {
            return null;
        }

        // A character is its top folder; only a nested-only layout ("Muzan/Muzan/Muzan.def") keeps the
        // nested folder as its id, as before.
        var defFolder = Path.GetDirectoryName(chosen.FullPath)!;
        var nestedOnly = decision.Candidates.All(c => c.RelativePath.Contains('/'));
        var relativeFolder = Relativize(charsRoot, nestedOnly ? defFolder : topFolder);
        var parsedName = parsed?.Name;
        if (string.IsNullOrWhiteSpace(parsedName))
        {
            parsedName = Path.GetFileName(defFolder);
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
            DefPath = Relativize(rootPath, chosen.FullPath),
            FolderPath = relativeFolder,
            SpriteFile = parsed?.SpriteFile,
            Nested = !string.Equals(Path.GetFullPath(defFolder), Path.GetFullPath(topFolder), StringComparison.OrdinalIgnoreCase),
            ModifiedAtUtc = SafeLastWrite(chosen.FullPath),
            DefCandidates = decision.Candidates.Select(c => Relativize(rootPath, c.FullPath)).ToList(),
            PrimaryRule = decision.Rule,
            NeedsDefChoice = decision.IsAmbiguous,
            Status = context?.Roster is null ? ContentStatus.Unregistered : roster.Status,
            ActiveDefPaths = roster.Active,
            DisabledDefPaths = roster.Disabled
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
