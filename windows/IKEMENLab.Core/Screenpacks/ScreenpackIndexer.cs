using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.SelectDef;

namespace IKEMENLab.Core.Screenpacks;

[Flags]
public enum ScreenpackComponents
{
    None = 0,
    TitleScreen = 1 << 0,
    SelectScreen = 1 << 1,
    VsScreen = 1 << 2,
    Lifebars = 1 << 3,
    ContinueScreen = 1 << 4,
    VictoryScreen = 1 << 5,
    OptionsScreen = 1 << 6,
    Storyboard = 1 << 7
}

public sealed record ScreenpackEntry
{
    /// <summary>Folder relative to the root ("data/ikemen1"), or "data" for data/system.def.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }

    /// <summary>Root-relative system.def path with forward slashes.</summary>
    public required string DefPath { get; init; }

    public string? SpriteFile { get; init; }
    public (int Width, int Height)? LocalCoord { get; init; }
    public ScreenpackComponents Components { get; init; }
    public int SelectRows { get; init; }
    public int SelectColumns { get; init; }
    public bool IsActive { get; init; }
    public DateTime? ModifiedAtUtc { get; init; }

    public int CharacterSlots => SelectRows * SelectColumns;

    public string ResolutionText => LocalCoord is { } c ? $"{c.Width}x{c.Height}" : "Unknown";

    public string SlotsText => SelectRows > 0 && SelectColumns > 0
        ? $"{CharacterSlots} slots ({SelectRows}×{SelectColumns})"
        : "Unknown";

    /// <summary>macOS categorisation: Lifebar / Storyboard / Screenpack.</summary>
    public string PrimaryType => Components switch
    {
        ScreenpackComponents.Lifebars => "Lifebar",
        ScreenpackComponents.Storyboard => "Storyboard",
        _ when Components.HasFlag(ScreenpackComponents.Storyboard) &&
               (Components & ~(ScreenpackComponents.Storyboard | ScreenpackComponents.TitleScreen)) == 0 => "Storyboard",
        _ => "Screenpack"
    };

    public IEnumerable<string> ComponentNames()
    {
        if (Components.HasFlag(ScreenpackComponents.TitleScreen)) yield return "Title";
        if (Components.HasFlag(ScreenpackComponents.SelectScreen)) yield return "Select";
        if (Components.HasFlag(ScreenpackComponents.VsScreen)) yield return "VS";
        if (Components.HasFlag(ScreenpackComponents.Lifebars)) yield return "Lifebars";
        if (Components.HasFlag(ScreenpackComponents.ContinueScreen)) yield return "Continue";
        if (Components.HasFlag(ScreenpackComponents.VictoryScreen)) yield return "Victory";
        if (Components.HasFlag(ScreenpackComponents.OptionsScreen)) yield return "Options";
        if (Components.HasFlag(ScreenpackComponents.Storyboard)) yield return "Storyboard";
    }
}

/// <summary>
/// Read-only screenpack (motif) discovery: data/system.def plus every data/&lt;folder&gt;/system.def.
/// Components come from real sections/[Files] keys rather than substring matches, and the active
/// motif is compared as a normalised path against save/config.ini.
/// </summary>
public static class ScreenpackIndexer
{
    public static IReadOnlyList<ScreenpackEntry> Index(string root, string? activeMotif)
    {
        var data = Path.Combine(root, "data");
        var results = new List<ScreenpackEntry>();
        if (!Directory.Exists(data)) return results;

        var activeFull = Path.GetFullPath(Path.Combine(root,
            SelectDefReader.NormalizeSeparators(string.IsNullOrWhiteSpace(activeMotif) ? SelectDefLocator.DefaultMotif : activeMotif.Trim())));

        var candidates = new List<string>();
        var rootDef = Path.Combine(data, "system.def");
        if (File.Exists(rootDef)) candidates.Add(rootDef);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(data).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var def = Path.Combine(dir, "system.def");
                if (File.Exists(def)) candidates.Add(def);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // The configured motif may live elsewhere (e.g. a custom path); include it once.
        if (File.Exists(activeFull) && !candidates.Any(c => SamePath(c, activeFull))) candidates.Add(activeFull);

        foreach (var def in candidates)
        {
            var entry = Read(root, def, SamePath(def, activeFull));
            if (entry is not null) results.Add(entry);
        }

        return results
            .OrderByDescending(s => s.IsActive)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ScreenpackEntry? Read(string root, string defPath, bool isActive)
    {
        var parsed = DefParser.ParseFile(defPath);
        if (parsed is null) return null;

        var folder = Path.GetDirectoryName(defPath)!;
        var id = Path.GetRelativePath(root, folder).Replace('\\', '/');
        var name = parsed.Value("name", "info");
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(folder).Replace('_', ' ');

        return new ScreenpackEntry
        {
            Id = id,
            Name = name.Trim(),
            Author = string.IsNullOrWhiteSpace(parsed.Value("author", "info")) ? "Unknown" : parsed.Value("author", "info")!.Trim(),
            DefPath = Path.GetRelativePath(root, defPath).Replace('\\', '/'),
            SpriteFile = ResolveMotifFile(root, folder, parsed.Value("spr", "files")),
            LocalCoord = ParseCoord(parsed.Value("localcoord", "info")),
            Components = DetectComponents(parsed, root, folder),
            SelectRows = ParseInt(parsed.Value("rows", "select info")),
            SelectColumns = ParseInt(parsed.Value("columns", "select info")),
            IsActive = isActive,
            ModifiedAtUtc = SafeLastWrite(defPath)
        };
    }

    internal static ScreenpackComponents DetectComponents(DefParseResult parsed, string root, string folder)
    {
        var c = ScreenpackComponents.None;
        bool Section(string name) => parsed.SectionValues.ContainsKey(name);
        bool FileKey(string key) => ResolveMotifFile(root, folder, parsed.Value(key, "files")) is not null;

        if (Section("title info") || Section("titlebgdef")) c |= ScreenpackComponents.TitleScreen;
        if (Section("select info")) c |= ScreenpackComponents.SelectScreen;
        if (Section("vs screen")) c |= ScreenpackComponents.VsScreen;
        if (FileKey("fight")) c |= ScreenpackComponents.Lifebars;
        if (Section("continue screen")) c |= ScreenpackComponents.ContinueScreen;
        if (Section("victory screen")) c |= ScreenpackComponents.VictoryScreen;
        if (Section("option info")) c |= ScreenpackComponents.OptionsScreen;
        if (FileKey("logo.storyboard") || FileKey("intro.storyboard")) c |= ScreenpackComponents.Storyboard;
        return c;
    }

    /// <summary>IKEMEN motif file lookup: motif folder, root, data/.</summary>
    public static string? ResolveMotifFile(string root, string motifFolder, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var normalized = SelectDefReader.NormalizeSeparators(reference.Trim());
        if (Path.IsPathRooted(normalized)) return File.Exists(normalized) ? normalized : null;
        foreach (var dir in new[] { motifFolder, root, Path.Combine(root, "data") })
        {
            var candidate = Path.Combine(dir, normalized);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static (int, int)? ParseCoord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(',');
        return parts.Length >= 2 && int.TryParse(parts[0].Trim(), out var w) && int.TryParse(parts[1].Trim(), out var h) && w > 0 && h > 0
            ? (w, h)
            : null;
    }

    private static int ParseInt(string? value) => int.TryParse(value?.Trim(), out var n) && n > 0 ? n : 0;

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static DateTime? SafeLastWrite(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
