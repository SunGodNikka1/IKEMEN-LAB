using IKEMENLab.Core.Library;
using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Install;

/// <summary>
/// Detects character / stage / screenpack packages from extracted folders with bounded wrapper-folder handling.
/// Uses Windows DefContentClassifier semantics. Rejects ambiguous mixes rather than guessing.
/// Character folders follow extraction structure (<see cref="CharacterLayoutAnalyzer"/>) and their primary
/// DEF is decided separately (<see cref="PrimaryDefResolver"/>); a DEF file name never names a folder.
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
        var candidates = FindPackageCandidates(cleaned, out var characterDefFolders);

        if (candidates.Count == 0 && characterDefFolders.Count == 0)
            throw new InvalidOperationException(
                "Could not determine content type. Need a character DEF, stage DEF, or screenpack system.def with UI evidence.");

        var stagePkgs = candidates.Where(c => c.Kind == InstallContentKind.Stage).ToList();
        var screenPkgs = candidates.Where(c => c.Kind == InstallContentKind.Screenpack).ToList();

        var kindCount = (characterDefFolders.Count > 0 ? 1 : 0) + (stagePkgs.Count > 0 ? 1 : 0) + (screenPkgs.Count > 0 ? 1 : 0);
        if (kindCount > 1)
        {
            throw new InvalidOperationException(
                "Ambiguous content: archive/folder mixes character, stage, and/or screenpack packages.");
        }

        if (characterDefFolders.Count > 0)
            return DetectCharacters(cleaned, sourceInput, characterDefFolders);

        var byRoot = new Dictionary<string, DetectedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!byRoot.ContainsKey(c.PackageRoot))
                byRoot[c.PackageRoot] = Enrich(c, sourceInput);
        }

        return byRoot.Values.ToList();
    }

    /// <summary>
    /// Characters: one package per unit found by extraction structure. A dropped folder is its own
    /// "archive"; an archive's loose files take the archive's name.
    /// </summary>
    private static IReadOnlyList<DetectedPackage> DetectCharacters(string payloadRoot, string sourceInput, IReadOnlyList<string> defFolders)
    {
        var payloadIsFolder = !File.Exists(sourceInput);
        var payloadName = payloadIsFolder
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(payloadRoot))
            : CharacterLayoutAnalyzer.ArchiveBaseName(sourceInput);

        var packages = new List<DetectedPackage>();
        foreach (var unit in CharacterLayoutAnalyzer.Analyze(payloadRoot, payloadName, payloadIsFolder, defFolders))
        {
            var decision = PrimaryDefResolver.Resolve(unit.UnitRoot, unit.DestinationName);
            if (decision.Candidates.Count == 0)
            {
                // A unit grown to include shared files can hold its DEFs deeper than one level.
                var unitPrefix = unit.UnitRoot + Path.DirectorySeparatorChar;
                var deep = defFolders
                    .Where(d => Path.GetFullPath(d).StartsWith(unitPrefix, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(d => Directory.EnumerateFiles(d, "*.def").Where(DefContentClassifier.IsValidCharacterDefFile))
                    .Select(p => PrimaryDefResolver.Describe(Path.GetFullPath(p), unit.UnitRoot))
                    .OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                decision = PrimaryDefResolver.Decide(deep, unit.DestinationName);
            }

            var display = decision.DisplayCandidate;
            if (display is null) continue;

            var warnings = new List<string>();
            if (decision.Primary is { IsComplete: false } incomplete)
                warnings.Add("Missing referenced file(s): " + string.Join(", ", incomplete.MissingFiles));

            packages.Add(new DetectedPackage
            {
                Kind = InstallContentKind.Character,
                DisplayName = display.CharacterName,
                SuggestedFolderName = unit.DestinationName,
                PackageRoot = unit.UnitRoot,
                DefPath = display.FullPath,
                SourceInput = sourceInput,
                Warnings = warnings,
                FileCount = SafeFileCount(unit.UnitRoot),
                NameSource = unit.NameSource,
                DefCandidates = decision.Candidates,
                PrimaryDef = decision.Primary,
                PrimaryRule = decision.Rule,
                Wrappers = unit.Wrappers,
                LeftOutDocs = unit.LeftOutDocs,
                LeftOutFiles = unit.LeftOutFiles
            });
        }

        if (packages.Count == 0)
            throw new InvalidOperationException("Could not determine content type. No usable character DEF was found.");
        return packages;
    }

    private static int SafeFileCount(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
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

    private static List<DetectedPackage> FindPackageCandidates(string root, out IReadOnlyList<string> characterDefFolders)
    {
        var results = new List<DetectedPackage>();
        var charFolders = new List<string>();
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
                    if (!charFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) charFolders.Add(dir);
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
        characterDefFolders = charFolders;

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
