using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Install;

/// <summary>A referenced file that needs an additional destination beyond the ordinary stage-folder copy.</summary>
/// <param name="SourcePath">The file in the extracted archive / dropped folder.</param>
/// <param name="Destination">Where it installs, relative to the IKEMEN root, '/'-separated.</param>
/// <param name="Reference">The DEF value that named it, as written.</param>
public sealed record StageCompanion(string SourcePath, string Destination, string Reference);

public sealed class StageAssetPlan
{
    public IReadOnlyList<StageCompanion> Companions { get; init; } = [];
    /// <summary>References that resolve nowhere: not in the stage folder, the archive, or the IKEMEN install.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Finds the files a stage DEF references and decides where each installs, without touching the DEF.
/// IKEMEN looks for a referenced file next to the DEF, then under the IKEMEN root (root, data/, sound/, stages/).
/// The archive is searched the same way, treating each folder above the DEF as a possible root, so an
/// author's layout (stages/x.def + sound/x.mp3, or Pack/Stage/x.def + Pack/sound/x.mp3) resolves to the same
/// files after install. File extensions play no part.
/// </summary>
public static class StageAssetResolver
{
    private static readonly string[] RootSubfolders = ["", "data", "sound", "stages"];

    /// <param name="defPaths">Every stage DEF that installs together.</param>
    /// <param name="defDirectory">Where those DEFs sit in the source.</param>
    /// <param name="scanRoot">The top of the extracted archive / dropped folder; nothing outside it is read.</param>
    /// <param name="ikemenRoot">The IKEMEN install.</param>
    /// <param name="stageDestination">Where the DEFs' folder installs, relative to the IKEMEN root ("stages" or "stages/Name").</param>
    public static StageAssetPlan Resolve(
        IReadOnlyList<string> defPaths, string defDirectory, string scanRoot, string ikemenRoot, string stageDestination)
    {
        var companions = new Dictionary<string, StageCompanion>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var warnings = new List<string>();
        var scanFull = Full(scanRoot);
        var defDirFull = Full(defDirectory);
        var ikemenFull = Full(ikemenRoot);

        foreach (var reference in defPaths.SelectMany(CollectReferences).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rel = reference.Replace('\\', '/').TrimStart('/');
            var local = Path.GetFullPath(Path.Combine(defDirFull, rel.Replace('/', Path.DirectorySeparatorChar)));

            // 1. Where IKEMEN looks first: next to the DEF. A '..' path can leave the stage folder and still be in the archive.
            if (File.Exists(local) && IsUnder(scanFull, local))
            {
                if (!IsUnder(defDirFull, local))
                    AddByOffset(companions, warnings, local, stageDestination, rel, reference, ikemenFull);
                continue;
            }

            // 2. Somewhere in the archive that IKEMEN's own search would reach once installed.
            var found = rel.Split('/').Contains("..") ? null : FindInArchive(defDirFull, scanFull, rel);
            if (found is { } hit)
            {
                AddMirrored(companions, warnings, hit.Path, hit.Sub, rel, reference, stageDestination, ikemenFull);
                continue;
            }

            // Some archives flatten an IKEMEN-root layout: e.g. X Factor.mp3 beside the DEF,
            // but bgmusic still says sound/X Factor.mp3. Keep the authored reference and install
            // that exact bundled path at its declared root location. Never search arbitrary basenames.
            var loose = FindFlattenedReference(defDirFull, scanFull, rel);
            if (loose is not null)
            {
                var ordinaryDestination = stageDestination + "/" +
                    Path.GetRelativePath(defDirFull, loose).Replace('\\', '/');
                // Flat stages already put stages/X.sff in the right place; do not plan it twice.
                if (!string.Equals(ordinaryDestination, rel, StringComparison.OrdinalIgnoreCase))
                    AddMirrored(companions, warnings, loose, "", rel, reference, stageDestination, ikemenFull);
                continue;
            }

            // 3. Already in the IKEMEN install (shared music, data/ assets).
            if (ExistsInInstall(ikemenFull, stageDestination, rel)) continue;

            missing.Add(reference);
            warnings.Add($"Missing referenced file: {reference} is not in the archive or your IKEMEN folder. " +
                         "The stage will install but that file will be missing in game.");
        }

        return new StageAssetPlan { Companions = companions.Values.ToList(), Missing = missing, Warnings = warnings };
    }

    /// <summary>Files a stage DEF names: sprites, sounds and music (including prefixed .bgmusic keys).</summary>
    public static IReadOnlyList<string> CollectReferences(string defPath)
    {
        var parsed = DefParser.ParseFile(defPath);
        if (parsed is null) return [];

        var found = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();
            if (value.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(value)) return;
            found.Add(value);
        }

        foreach (var key in new[] { "sprite", "spr", "bgmusic", "bgm", "music", "snd", "sound" })
            Add(parsed.Value(key));

        foreach (var (section, values) in parsed.SectionValues)
        {
            if (section.StartsWith("bg", StringComparison.OrdinalIgnoreCase))
            {
                if (values.TryGetValue("spr", out var spr)) Add(spr);
            }
            else if (section.Equals("music", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (key, value) in values)
                    if (key.EndsWith("bgmusic", StringComparison.Ordinal) || key.EndsWith("bgm", StringComparison.Ordinal))
                        Add(value);
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (string Path, string Sub)? FindInArchive(string defDir, string scanRoot, string rel)
    {
        var relOs = rel.Replace('/', Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(defDir);
        while (dir is not null && IsUnderOrSame(scanRoot, dir))
        {
            foreach (var sub in RootSubfolders)
            {
                var candidate = Path.GetFullPath(Path.Combine(dir, sub, relOs));
                if (File.Exists(candidate) && IsUnder(scanRoot, candidate)) return (candidate, sub);
            }

            if (SamePath(dir, scanRoot)) break;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>Recognizes only a known root prefix omitted from the bundled folder layout.</summary>
    private static string? FindFlattenedReference(string defDir, string scanRoot, string rel)
    {
        var slash = rel.IndexOf('/');
        if (slash <= 0 || !RootSubfolders.Any(sub => sub.Length > 0 &&
            sub.Equals(rel[..slash], StringComparison.OrdinalIgnoreCase))) return null;
        var remainder = rel[(slash + 1)..];
        if (remainder.Length == 0 || remainder.Split('/').Contains("..")) return null;
        var candidate = Path.GetFullPath(Path.Combine(defDir, remainder.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(candidate) && IsUnder(defDir, candidate) && IsUnder(scanRoot, candidate)
            ? candidate : null;
    }

    /// <summary>A '..' reference: keep the exact relative position to the stage folder.</summary>
    private static void AddByOffset(Dictionary<string, StageCompanion> companions, List<string> warnings,
        string source, string stageDestination, string rel, string reference, string ikemenRoot)
    {
        var dest = NormalizeRelative(stageDestination + "/" + rel);
        if (dest is null)
        {
            warnings.Add($"Referenced file {reference} would install outside your IKEMEN folder, so it was not included.");
            return;
        }

        AddChecked(companions, warnings, source, dest, reference, ikemenRoot, relocateTo: null);
    }

    /// <summary>A file found under an archive folder that plays the IKEMEN root: mirror it to the same place under the install.</summary>
    private static void AddMirrored(Dictionary<string, StageCompanion> companions, List<string> warnings,
        string source, string sub, string rel, string reference, string stageDestination, string ikemenRoot)
    {
        var normalized = NormalizeRelative(rel);
        if (normalized is null)
        {
            warnings.Add($"Referenced file {reference} would install outside your IKEMEN folder, so it was not included.");
            return;
        }

        // A bare file name found loose above the stage folder belongs beside the stage, not in the IKEMEN root.
        var dest = sub.Length == 0 && !normalized.Contains('/')
            ? stageDestination + "/" + normalized
            : (sub.Length == 0 ? normalized : sub + "/" + normalized);

        // If the shared spot already holds a different file, keep the author's file inside the stage folder,
        // where IKEMEN looks first, rather than overwriting something other content may use.
        var relocate = stageDestination + "/" + normalized;
        AddChecked(companions, warnings, source, dest, reference, ikemenRoot, relocate);
    }

    private static void AddChecked(Dictionary<string, StageCompanion> companions, List<string> warnings,
        string source, string dest, string reference, string ikemenRoot, string? relocateTo)
    {
        var existing = Path.Combine(ikemenRoot, dest.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(existing))
        {
            if (SameContent(existing, source)) return; // already installed, identical
            if (relocateTo is not null && !File.Exists(Path.Combine(ikemenRoot, relocateTo.Replace('/', Path.DirectorySeparatorChar))))
            {
                warnings.Add($"{dest} already exists and is different, so {reference} installs inside the stage folder instead.");
                dest = relocateTo;
            }
            else
            {
                warnings.Add($"{dest} already exists and is different; it was not overwritten, so {reference} may not play as the author intended.");
                return;
            }
        }

        companions[dest] = new StageCompanion(source, dest, reference);
    }

    private static bool ExistsInInstall(string ikemenRoot, string stageDestination, string rel)
    {
        var relOs = rel.Replace('/', Path.DirectorySeparatorChar);
        var stageDir = Path.Combine(ikemenRoot, stageDestination.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(Path.GetFullPath(Path.Combine(stageDir, relOs)))) return true;
        return RootSubfolders.Any(sub => File.Exists(Path.GetFullPath(Path.Combine(ikemenRoot, sub, relOs))));
    }

    /// <summary>Collapses '.'/'..'; null when the result leaves the IKEMEN root.</summary>
    private static string? NormalizeRelative(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SamePath(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    private static bool IsUnder(string parent, string child) =>
        Full(child).StartsWith(Full(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsUnderOrSame(string parent, string child) => SamePath(parent, child) || IsUnder(parent, child);
}
