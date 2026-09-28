using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Install;

/// <summary>Where a character package's destination folder name came from.</summary>
public enum FolderNameSource
{
    /// <summary>Not a character package (stages and screenpacks keep their own naming).</summary>
    Detected,

    /// <summary>The package's own character folder, kept exactly ("Extract Here").</summary>
    CharacterFolder,

    /// <summary>Loose files at the archive root: the archive's file name ("Extract to Name\").</summary>
    ArchiveName,

    /// <summary>An already-extracted folder given directly: its own name.</summary>
    DroppedFolder
}

/// <summary>One character package found in an extracted archive or a dropped folder.</summary>
public sealed record CharacterUnit
{
    /// <summary>The folder that is installed as chars/&lt;DestinationName&gt;.</summary>
    public required string UnitRoot { get; init; }

    public required string DestinationName { get; init; }
    public required FolderNameSource NameSource { get; init; }

    /// <summary>Packaging folders above the unit that are not installed (relative to the payload).</summary>
    public IReadOnlyList<string> Wrappers { get; init; } = [];

    /// <summary>Documentation outside the unit that is not installed (readme, screenshots, links).</summary>
    public IReadOnlyList<string> LeftOutDocs { get; init; } = [];

    /// <summary>Other files outside the unit. Their role is unknown, so installing without them must be confirmed.</summary>
    public IReadOnlyList<string> LeftOutFiles { get; init; } = [];
}

/// <summary>
/// Decides character package folders the way extracting the archive would, never from DEF file names:
/// <list type="bullet">
/// <item>A <b>DEF folder</b> directly contains a valid character DEF. A <b>unit</b> is a DEF folder not
/// inside another one; everything below it (AI patches, alternates) belongs to it.</item>
/// <item>A unit that references files outside itself ("../common/x.sff") grows to the folder containing them.</item>
/// <item><b>Flat</b>: the unit is the payload root → named after the archive (or the dropped folder itself).</item>
/// <item><b>Real character folder</b>: the unit is below the payload root → its own folder name, exactly.</item>
/// <item><b>Wrapper</b>: a folder between the payload root and units. It is dropped by structure alone; its
/// documentation is left out, and any other files make the layout ambiguous (the user confirms).</item>
/// <item><b>Several units</b> not inside each other → one package each.</item>
/// </list>
/// </summary>
public static class CharacterLayoutAnalyzer
{
    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".nfo", ".diz", ".url", ".htm", ".html", ".pdf", ".md", ".rtf", ".doc", ".docx", ".odt",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".lnk"
    };

    private static readonly string[] DocPrefixes = ["readme", "read me", "license", "licence", "changelog", "credits", "instructions"];

    private static readonly string[] ReferenceKeys =
    [
        "sprite", "anim", "cmd", "cns", "st", "st0", "st1", "st2", "st3", "st4", "st5", "st6", "st7", "st8", "st9",
        "stcommon", "sound", "ai", "movelist", "intro", "ending",
        "pal1", "pal2", "pal3", "pal4", "pal5", "pal6", "pal7", "pal8", "pal9", "pal10", "pal11", "pal12"
    ];

    /// <param name="payloadRoot">Extraction folder of an archive, or the dropped folder itself.</param>
    /// <param name="payloadName">Archive file name without extension, or the dropped folder's name.</param>
    /// <param name="characterDefFolders">Folders that directly contain a valid character DEF.</param>
    public static IReadOnlyList<CharacterUnit> Analyze(
        string payloadRoot, string payloadName, bool payloadIsFolder, IEnumerable<string> characterDefFolders)
    {
        var payload = Normalize(payloadRoot);
        var defFolders = characterDefFolders.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var units = Outermost(defFolders);

        // Grow units that reference files outside themselves, until stable.
        for (var pass = 0; pass < 8; pass++)
        {
            var changed = false;
            foreach (var defFolder in defFolders)
            {
                var unit = units.First(u => IsSameOrUnder(u, defFolder));
                foreach (var referenced in ReferencedFiles(defFolder))
                {
                    if (!IsSameOrUnder(payload, referenced) || IsSameOrUnder(unit, referenced)) continue;
                    var grown = CommonAncestor(unit, Path.GetDirectoryName(referenced)!);
                    if (!IsSameOrUnder(payload, grown)) continue;
                    units = Outermost(units.Where(u => u != unit).Append(grown).ToList());
                    unit = units.First(u => IsSameOrUnder(u, defFolder));
                    changed = true;
                }
            }

            if (!changed) break;
        }

        var containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
        {
            for (var dir = unit; !string.Equals(dir, payload, StringComparison.OrdinalIgnoreCase);)
            {
                dir = Path.GetDirectoryName(dir)!;
                containers.Add(dir);
            }
        }

        var results = new List<CharacterUnit>();
        foreach (var unit in units.OrderBy(u => u, StringComparer.OrdinalIgnoreCase))
        {
            var docs = new List<string>();
            var stray = new List<string>();
            var wrappers = new List<string>();
            foreach (var container in containers.Where(c => IsUnder(c, unit)).OrderBy(c => c.Length))
            {
                if (!string.Equals(container, payload, StringComparison.OrdinalIgnoreCase) || payloadIsFolder)
                    wrappers.Add(DisplayPath(payload, container, payloadName));
                ClassifyContainer(payload, payloadName, container, units, containers, docs, stray);
            }

            var flat = string.Equals(unit, payload, StringComparison.OrdinalIgnoreCase);
            results.Add(new CharacterUnit
            {
                UnitRoot = unit,
                DestinationName = flat ? payloadName : Path.GetFileName(unit),
                NameSource = flat
                    ? payloadIsFolder ? FolderNameSource.DroppedFolder : FolderNameSource.ArchiveName
                    : FolderNameSource.CharacterFolder,
                Wrappers = wrappers,
                LeftOutDocs = docs,
                LeftOutFiles = stray
            });
        }

        return results;
    }

    /// <summary>"Muzan V3.zip" → "Muzan V3"; "Muzan.part1.rar" → "Muzan". Otherwise kept exactly.</summary>
    public static string ArchiveBaseName(string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);
        var part = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (part > 0 && name.Length > part + 5 && name[(part + 5)..].All(char.IsAsciiDigit))
            name = name[..part];
        return name;
    }

    public static bool IsDocumentation(string fileName)
        => DocExtensions.Contains(Path.GetExtension(fileName)) ||
           DocPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsJunk(string name)
        => string.IsNullOrWhiteSpace(name) ||
           name.StartsWith('.') ||
           name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);

    private static void ClassifyContainer(string payload, string payloadName, string container, IReadOnlyList<string> units,
        HashSet<string> containers, List<string> docs, List<string> stray)
    {
        foreach (var file in SafeFiles(container))
        {
            var name = Path.GetFileName(file);
            if (IsJunk(name)) continue;
            (IsDocumentation(name) ? docs : stray).Add(DisplayPath(payload, file, payloadName));
        }

        foreach (var sub in SafeDirectories(container))
        {
            if (IsJunk(Path.GetFileName(sub))) continue;
            if (units.Any(u => string.Equals(u, sub, StringComparison.OrdinalIgnoreCase)) || containers.Contains(sub)) continue;

            var files = SafeFilesRecursive(sub).Where(f => !IsJunk(Path.GetFileName(f))).ToList();
            if (files.Count == 0) continue;
            (files.All(f => IsDocumentation(Path.GetFileName(f))) ? docs : stray).Add(DisplayPath(payload, sub, payloadName) + "/");
        }
    }

    private static IEnumerable<string> ReferencedFiles(string defFolder)
    {
        foreach (var def in SafeFiles(defFolder).Where(f => f.EndsWith(".def", StringComparison.OrdinalIgnoreCase)))
        {
            if (!DefContentClassifier.IsValidCharacterDefFile(def)) continue;
            DefParseResult? parsed;
            try { parsed = DefParser.ParseFile(def); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (parsed is null) continue;

            foreach (var key in ReferenceKeys)
            {
                var value = parsed.Value(key, "files")?.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(value)) continue;
                string full;
                try
                {
                    full = Path.GetFullPath(Path.Combine(defFolder, value.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (File.Exists(full)) yield return full;
            }
        }
    }

    private static List<string> Outermost(List<string> folders)
        => folders.Where(f => !folders.Any(o => !string.Equals(o, f, StringComparison.OrdinalIgnoreCase) && IsUnder(o, f)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string CommonAncestor(string a, string b)
    {
        var x = a;
        while (!IsSameOrUnder(x, b)) x = Path.GetDirectoryName(x) ?? x;
        return x;
    }

    private static string DisplayPath(string payload, string path, string payloadName)
    {
        var relative = Path.GetRelativePath(payload, path).Replace('\\', '/');
        return relative == "." ? payloadName : relative;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsUnder(string parent, string child)
        => child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrUnder(string parent, string child)
        => string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) || IsUnder(parent, child);

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFilesRecursive(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(500).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
