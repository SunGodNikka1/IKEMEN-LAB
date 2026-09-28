using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Install;

/// <summary>
/// Detects character / stage / screenpack packages from extracted folders with bounded wrapper-folder handling.
/// Uses Windows DefContentClassifier semantics. Rejects ambiguous mixes rather than guessing.
/// </summary>
public static class ContentDetector
{
    public const int MaxScanDepth = 4;
    public const int MaxDefsToScan = 200;

    public static IReadOnlyList<DetectedPackage> DetectFromDirectory(string directory, string sourceInput)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var cleaned = StripJunk(directory);
        var candidates = FindPackageCandidates(cleaned);

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "Could not determine content type. Need a character DEF, stage DEF, or screenpack system.def with UI evidence.");

        var charPkgs = candidates.Where(c => c.Kind == InstallContentKind.Character).ToList();
        var stagePkgs = candidates.Where(c => c.Kind == InstallContentKind.Stage).ToList();
        var screenPkgs = candidates.Where(c => c.Kind == InstallContentKind.Screenpack).ToList();

        var kindCount = (charPkgs.Count > 0 ? 1 : 0) + (stagePkgs.Count > 0 ? 1 : 0) + (screenPkgs.Count > 0 ? 1 : 0);
        if (kindCount > 1)
        {
            throw new InvalidOperationException(
                "Ambiguous content: archive/folder mixes character, stage, and/or screenpack packages.");
        }

        var byRoot = new Dictionary<string, DetectedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!byRoot.ContainsKey(c.PackageRoot))
                byRoot[c.PackageRoot] = Enrich(c, sourceInput);
        }

        return byRoot.Values.ToList();
    }

    private static DetectedPackage Enrich(DetectedPackage draft, string sourceInput)
    {
        var warnings = new List<string>(draft.Warnings);
        var missing = new List<string>();

        if (draft.Kind == InstallContentKind.Stage)
        {
            foreach (var asset in CollectReferencedAssets(draft.DefPath, draft.PackageRoot))
            {
                var full = Path.GetFullPath(Path.Combine(draft.PackageRoot, asset.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(full))
                {
                    missing.Add(asset);
                    warnings.Add($"Missing referenced asset: {asset}");
                }
            }
        }

        var fileCount = Directory.Exists(draft.PackageRoot)
            ? Directory.EnumerateFiles(draft.PackageRoot, "*", SearchOption.AllDirectories).Count()
            : 0;

        return new DetectedPackage
        {
            Kind = draft.Kind,
            DisplayName = draft.DisplayName,
            SuggestedFolderName = SanitizeFolderName(draft.SuggestedFolderName),
            PackageRoot = draft.PackageRoot,
            DefPath = draft.DefPath,
            SourceInput = sourceInput,
            Warnings = warnings,
            MissingAssets = missing,
            FileCount = fileCount
        };
    }

    private static List<DetectedPackage> FindPackageCandidates(string root)
    {
        var results = new List<DetectedPackage>();
        var defsScanned = 0;

        void Walk(string dir, int depth)
        {
            if (depth > MaxScanDepth || defsScanned >= MaxDefsToScan) return;

            IEnumerable<string> defs;
            try
            {
                defs = Directory.EnumerateFiles(dir, "*.def");
            }
            catch
            {
                return;
            }

            foreach (var def in defs)
            {
                if (defsScanned >= MaxDefsToScan) return;
                defsScanned++;

                var name = Path.GetFileName(def);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;

                // Screenpack before character/stage — system.def with motif evidence.
                if (DefContentClassifier.IsValidScreenpackDefFile(def))
                {
                    results.Add(BuildScreenpackCandidate(def, dir));
                    continue;
                }

                if (DefContentClassifier.IsValidCharacterDefFile(def))
                {
                    results.Add(BuildCharacterCandidate(def, dir));
                    continue;
                }

                if (DefContentClassifier.IsValidStageDefFile(def))
                {
                    results.Add(BuildStageCandidate(def, dir));
                }
            }

            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                return;
            }

            foreach (var sub in subdirs)
            {
                var leaf = Path.GetFileName(sub);
                if (IsJunkName(leaf)) continue;
                Walk(sub, depth + 1);
            }
        }

        Walk(root, 0);

        // Prefer shallower package roots: if both parent and child match same kind with child
        // containing the only DEF, keep child. If parent has DEF, keep parent.
        return PruneNestedDuplicates(results);
    }

    private static List<DetectedPackage> PruneNestedDuplicates(List<DetectedPackage> items)
    {
        if (items.Count <= 1) return items;

        var keep = new List<DetectedPackage>();
        foreach (var item in items.OrderBy(i => i.PackageRoot.Length))
        {
            // Skip if a kept package root already contains this one (nested duplicate of same kind).
            var nestedUnderKept = keep.Any(k =>
                k.Kind == item.Kind &&
                IsUnder(k.PackageRoot, item.PackageRoot) &&
                !string.Equals(k.PackageRoot, item.PackageRoot, StringComparison.OrdinalIgnoreCase));

            if (nestedUnderKept) continue;

            // If this item is a parent of an already-kept deeper package of same kind, replace deeper
            // only when the DEF lives at this parent (already true for item.PackageRoot == def dir).
            keep.RemoveAll(k =>
                k.Kind == item.Kind &&
                IsUnder(item.PackageRoot, k.PackageRoot) &&
                !string.Equals(k.PackageRoot, item.PackageRoot, StringComparison.OrdinalIgnoreCase));

            keep.Add(item);
        }

        return keep;
    }

    private static DetectedPackage BuildCharacterCandidate(string defPath, string defDirectory)
    {
        var parsed = DefParser.ParseFile(defPath);
        var folderName = Path.GetFileNameWithoutExtension(defPath);
        var display = parsed?.EffectiveName ?? folderName;
        return new DetectedPackage
        {
            Kind = InstallContentKind.Character,
            DisplayName = display,
            SuggestedFolderName = folderName,
            PackageRoot = defDirectory,
            DefPath = defPath,
            SourceInput = string.Empty
        };
    }

    private static DetectedPackage BuildStageCandidate(string defPath, string defDirectory)
    {
        var display = StageNameExtractor.Extract(defPath);
        var folderName = Path.GetFileNameWithoutExtension(defPath);
        return new DetectedPackage
        {
            Kind = InstallContentKind.Stage,
            DisplayName = display,
            SuggestedFolderName = folderName,
            PackageRoot = defDirectory,
            DefPath = defPath,
            SourceInput = string.Empty
        };
    }

    private static DetectedPackage BuildScreenpackCandidate(string defPath, string defDirectory)
    {
        var parsed = DefParser.ParseFile(defPath);
        var folderName = Path.GetFileName(defDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName) || folderName.Equals("data", StringComparison.OrdinalIgnoreCase))
            folderName = parsed?.Value("name", "info")?.Trim() ?? "screenpack";
        var display = string.IsNullOrWhiteSpace(parsed?.Value("name", "info"))
            ? folderName.Replace('_', ' ')
            : parsed!.Value("name", "info")!.Trim();
        return new DetectedPackage
        {
            Kind = InstallContentKind.Screenpack,
            DisplayName = display,
            SuggestedFolderName = folderName,
            PackageRoot = defDirectory,
            DefPath = defPath,
            SourceInput = string.Empty
        };
    }

    /// <summary>
    /// True when the stage package should install as loose files directly under stages/
    /// (classic MUGEN layout). Folder packages install as stages/Name/.
    /// </summary>
    public static bool IsFlatStageLayout(DetectedPackage package, string scanRoot)
    {
        if (package.Kind != InstallContentKind.Stage) return false;

        // DEF must be at package root.
        if (!string.Equals(
                Path.GetDirectoryName(package.DefPath),
                package.PackageRoot,
                StringComparison.OrdinalIgnoreCase))
            return false;

        // Any non-junk subdirectory with files => folder package.
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(package.PackageRoot))
            {
                if (IsJunkName(Path.GetFileName(sub))) continue;
                if (Directory.EnumerateFileSystemEntries(sub).Any())
                    return false;
            }
        }
        catch
        {
            return false;
        }

        var packageFull = Path.GetFullPath(package.PackageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var scanFull = Path.GetFullPath(scanRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Flat only when loose files sit at the scan/staging root itself.
        if (!string.Equals(packageFull, scanFull, StringComparison.OrdinalIgnoreCase))
            return false;

        // A user-dropped/named folder (FolderName.def inside FolderName/) is a folder package,
        // not classic flat stages/ loose files.
        var rootLeaf = Path.GetFileName(packageFull);
        if (!string.IsNullOrEmpty(rootLeaf) &&
            string.Equals(rootLeaf, package.SuggestedFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static IReadOnlyList<string> CollectReferencedAssets(string defPath, string packageRoot)
    {
        var parsed = DefParser.ParseFile(defPath);
        if (parsed is null) return Array.Empty<string>();

        var keys = new[] { "sprite", "spr", "bgmusic", "bgm", "music", "snd", "sound" };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            var value = parsed.Value(key) ?? parsed.Value(key, "bgdef") ?? parsed.Value(key, "music") ?? parsed.Value(key, "info");
            if (string.IsNullOrWhiteSpace(value)) continue;
            // Skip URLs / absolute paths outside package
            if (value.Contains("://", StringComparison.Ordinal)) continue;
            found.Add(value.Replace('\\', '/'));
        }

        // Also scan [BG*] sections for spr= values
        foreach (var section in parsed.SectionValues)
        {
            if (!section.Key.StartsWith("bg", StringComparison.OrdinalIgnoreCase)) continue;
            if (section.Value.TryGetValue("spr", out var spr) && !string.IsNullOrWhiteSpace(spr))
                found.Add(spr.Replace('\\', '/'));
        }

        return found.ToList();
    }

    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) || ch == ',' ? '_' : ch).ToArray()).Trim();
        cleaned = cleaned.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? "Unnamed" : cleaned;
    }

    private static string StripJunk(string directory)
    {
        // If the only real child is a single wrapper folder, do not auto-unwrap here —
        // Walk() will find DEFs inside. Returning directory as-is is correct.
        return Path.GetFullPath(directory);
    }

    private static bool IsJunkName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.StartsWith('.')) return true;
        if (name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsUnder(string parent, string child)
    {
        var p = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(child);
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }
}
