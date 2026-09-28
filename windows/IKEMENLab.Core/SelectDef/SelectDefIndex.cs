using IKEMENLab.Core.Models;

namespace IKEMENLab.Core.SelectDef;

/// <summary>
/// Lookup of which DEFs the active select.def enables or comments out. Paths are compared as
/// normalised full paths, case-insensitively (Windows semantics).
/// </summary>
public sealed class SelectDefIndex
{
    private readonly string _root;
    private readonly HashSet<string> _activeCharacters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabledCharacters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeStages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabledStages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string FullPath, bool Active)> _characterRefs = [];

    private SelectDefIndex(string root, SelectDefLocation location, SelectDefDocument? document)
    {
        _root = root;
        Location = location;
        Document = document;
    }

    public SelectDefLocation Location { get; }
    public SelectDefDocument? Document { get; }
    public bool IsAvailable => Document is not null;

    /// <summary>Uncommented content lines whose DEF does not exist on disk.</summary>
    public IReadOnlyList<SelectDefEntry> MissingEntries { get; private set; } = Array.Empty<SelectDefEntry>();

    /// <summary>Uncommented lines IKEMEN would reject.</summary>
    public IReadOnlyList<SelectDefEntry> InvalidEntries { get; private set; } = Array.Empty<SelectDefEntry>();

    public static SelectDefIndex Build(string root, SelectDefLocation location)
    {
        var document = location.Path is null ? null : SelectDefReader.ReadFile(location.Path, root);
        var index = new SelectDefIndex(root, location, document);
        if (document is null) return index;

        var missing = new List<SelectDefEntry>();
        var invalid = new List<SelectDefEntry>();
        foreach (var entry in document.Entries)
        {
            if (entry.Kind == SelectDefEntryKind.Invalid)
            {
                if (!entry.IsCommented) invalid.Add(entry);
                continue;
            }

            if (entry.Kind != SelectDefEntryKind.Content) continue;

            if (entry.IsInArchive) continue;

            if (entry.ResolvedDefPath is null)
            {
                if (!entry.IsCommented) missing.Add(entry);
                continue;
            }

            var key = index.Key(entry.ResolvedDefPath);
            var (active, disabled) = entry.Section == SelectDefSection.Characters
                ? (index._activeCharacters, index._disabledCharacters)
                : (index._activeStages, index._disabledStages);
            if (entry.IsCommented) disabled.Add(key);
            else active.Add(key);
            if (entry.Section == SelectDefSection.Characters) index._characterRefs.Add((key, !entry.IsCommented));
        }

        index.MissingEntries = missing;
        index.InvalidEntries = invalid;
        return index;
    }

    public static SelectDefIndex Unavailable(string root)
        => new(root, new SelectDefLocation(null, SelectDefLocationSource.NotFound, null), null);

    /// <param name="rootRelativeDefPath">e.g. "chars/kfm/kfm.def".</param>
    public ContentStatus CharacterStatus(string rootRelativeDefPath)
        => StatusOf(rootRelativeDefPath, _activeCharacters, _disabledCharacters);

    /// <summary>
    /// Every character DEF below <paramref name="rootRelativeFolder"/> ("chars/Muzan") that select.def
    /// lists, as root-relative paths. This is how the roster knows which DEF of a package is actually
    /// used (e.g. "chars/Muzan/Muzan_AI.def"), not just whether the primary DEF is.
    /// </summary>
    public RosterReferences CharacterReferencesUnder(string rootRelativeFolder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Key(rootRelativeFolder)) + Path.DirectorySeparatorChar;
        var active = new List<string>();
        var disabled = new List<string>();
        foreach (var (full, isActive) in _characterRefs)
        {
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(_root, full).Replace('\\', '/');
            var list = isActive ? active : disabled;
            if (!list.Contains(relative, StringComparer.OrdinalIgnoreCase)) list.Add(relative);
        }

        // A DEF with both an uncommented and a commented line is in use.
        disabled.RemoveAll(d => active.Contains(d, StringComparer.OrdinalIgnoreCase));
        return new RosterReferences(active, disabled);
    }

    /// <param name="rootRelativeDefPath">e.g. "stages/kfm.def".</param>
    public ContentStatus StageStatus(string rootRelativeDefPath)
        => StatusOf(rootRelativeDefPath, _activeStages, _disabledStages);

    private ContentStatus StatusOf(string relative, HashSet<string> active, HashSet<string> disabled)
    {
        var key = Key(relative);
        if (active.Contains(key)) return ContentStatus.Active;
        if (disabled.Contains(key)) return ContentStatus.Disabled;
        return ContentStatus.Unregistered;
    }

    private string Key(string relative)
    {
        var combined = Path.IsPathRooted(relative) ? relative : Path.Combine(_root, relative);
        return Path.GetFullPath(combined);
    }
}

/// <summary>Root-relative character DEFs of one folder that select.def lists uncommented / commented.</summary>
public sealed record RosterReferences(IReadOnlyList<string> Active, IReadOnlyList<string> Disabled)
{
    public static RosterReferences None { get; } = new([], []);

    public ContentStatus Status => Active.Count > 0 ? ContentStatus.Active
        : Disabled.Count > 0 ? ContentStatus.Disabled
        : ContentStatus.Unregistered;
}
