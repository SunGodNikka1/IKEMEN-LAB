using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Library;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Install;

public enum FullgameDuplicateAction
{
    Replace,
    Skip,
    ReplaceAll,
    SkipAll
}

public enum FullgameItemCollision
{
    New,
    Existing,
    Conflict,
    Invalid
}

public sealed record FullgameCharacterItem(string FolderPath, string FolderName, string DefPath, FullgameItemCollision Collision);
public sealed record FullgameStageItem(string Path, string Name, bool IsLooseFile, FullgameItemCollision Collision);
public sealed record FullgameScreenpackItem(string DataFolderPath, string DisplayName, string SuggestedFolderName, FullgameItemCollision Collision);

public sealed class FullgameManifest
{
    public required string SourcePath { get; init; }
    public required string SourceFolderName { get; init; }
    public IReadOnlyList<FullgameCharacterItem> Characters { get; init; } = [];
    public IReadOnlyList<FullgameStageItem> Stages { get; init; } = [];
    public FullgameScreenpackItem? Screenpack { get; init; }
    public IReadOnlyList<string> Fonts { get; init; } = [];
    public IReadOnlyList<string> Sounds { get; init; } = [];

    public bool IsFullgame
    {
        get
        {
            var n = 0;
            if (Characters.Count > 0) n++;
            if (Stages.Count > 0) n++;
            if (Screenpack is not null) n++;
            return n >= 2;
        }
    }

    public string SuggestedCollectionName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Screenpack?.DisplayName))
                return "Imported — " + Screenpack!.DisplayName.Trim();
            return "Imported — " + CollectionNameResolver.DeriveNameFromFolder(SourceFolderName);
        }
    }
}

public sealed class FullgameImportResult
{
    public List<string> CharactersInstalled { get; } = [];
    public List<string> CharactersSkipped { get; } = [];
    public List<(string Name, string Error)> CharactersFailed { get; } = [];
    public List<string> StagesInstalled { get; } = [];
    public List<string> StagesSkipped { get; } = [];
    public List<(string Name, string Error)> StagesFailed { get; } = [];
    public string? ScreenpackInstalled { get; set; }
    public string? ScreenpackSkipped { get; set; }
    public string? ScreenpackFailed { get; set; }
    public List<string> FontsInstalled { get; } = [];
    public List<string> FontsSkipped { get; } = [];
    public List<string> SoundsInstalled { get; } = [];
    public List<string> SoundsSkipped { get; } = [];
    public CharacterCollection? CollectionCreated { get; set; }
    public List<string> OperationIds { get; } = [];

    public int TotalInstalled =>
        CharactersInstalled.Count + StagesInstalled.Count +
        (ScreenpackInstalled is null ? 0 : 1) +
        FontsInstalled.Count + SoundsInstalled.Count;

    public int TotalFailed =>
        CharactersFailed.Count + StagesFailed.Count + (ScreenpackFailed is null ? 0 : 1);

    public string Summary =>
        $"Installed {TotalInstalled}, skipped {CharactersSkipped.Count + StagesSkipped.Count + (ScreenpackSkipped is null ? 0 : 1) + FontsSkipped.Count + SoundsSkipped.Count}, failed {TotalFailed}.";
}

public static class CollectionNameResolver
{
    public static string DeriveNameFromFolder(string folderName)
    {
        var name = folderName.Trim();
        foreach (var suffix in new[] { ".zip", ".rar", ".7z", "_full", "-full", "_complete" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
        }

        return string.IsNullOrWhiteSpace(name) ? "Fullgame" : name.Replace('_', ' ').Trim();
    }

    public static string SanitizeForFolder(string name) => ContentDetector.SanitizeFolderName(name);
}

/// <summary>
/// Imports multi-content MUGEN/IKEMEN packages into an existing install (whitelist only).
/// Never copies engine binaries; never mutates select.def or Motif.
/// </summary>
public sealed class FullgameImporter
{
    private readonly IContentInstallService _installer;
    private readonly ISafeMutationService _mutations;
    private readonly CollectionStore _collections;
    private readonly string _stagingRoot;

    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ikemen_GO.exe", "Ikemen_GO", "mugen.exe", "mugen", "external", "mods", "juggernaut"
    };

    public FullgameImporter(
        IContentInstallService? installer = null,
        ISafeMutationService? mutations = null,
        CollectionStore? collections = null,
        string? stagingRoot = null,
        IKEMENLab.Core.Library.DateAddedTracker? dateAdded = null)
    {
        _mutations = mutations ?? new SafeMutationService();
        _installer = installer ?? new ContentInstallService(_mutations, dateAdded);
        _collections = collections ?? new CollectionStore();
        _stagingRoot = stagingRoot ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "fullgame-staging");
        Directory.CreateDirectory(_stagingRoot);
    }

    public FullgameManifest Scan(string packagePath, string? ikemenRootForCollision = null)
    {
        var root = Path.GetFullPath(packagePath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        RejectForbiddenContent(root);

        var charsDir = Path.Combine(root, "chars");
        var stagesDir = Path.Combine(root, "stages");
        var dataDir = Path.Combine(root, "data");
        var fontDir = Path.Combine(root, "font");
        var soundDir = Path.Combine(root, "sound");

        var characters = new List<FullgameCharacterItem>();
        if (Directory.Exists(charsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(charsDir))
            {
                var name = Path.GetFileName(dir);
                if (IsJunk(name)) continue;
                var def = FindCharacterDef(dir, name);
                if (def is null) continue;
                var collision = ikemenRootForCollision is null
                    ? FullgameItemCollision.New
                    : Directory.Exists(Path.Combine(ikemenRootForCollision, "chars", name))
                        ? FullgameItemCollision.Existing
                        : FullgameItemCollision.New;
                characters.Add(new FullgameCharacterItem(dir, name, def, collision));
            }
        }

        var stages = new List<FullgameStageItem>();
        if (Directory.Exists(stagesDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(stagesDir))
            {
                var name = Path.GetFileName(dir);
                if (IsJunk(name)) continue;
                var def = Directory.EnumerateFiles(dir, "*.def").FirstOrDefault(f => DefContentClassifier.IsValidStageDefFile(f));
                if (def is null) continue;
                var collision = ikemenRootForCollision is null
                    ? FullgameItemCollision.New
                    : Directory.Exists(Path.Combine(ikemenRootForCollision, "stages", name))
                        ? FullgameItemCollision.Existing
                        : FullgameItemCollision.New;
                stages.Add(new FullgameStageItem(dir, name, false, collision));
            }

            foreach (var def in Directory.EnumerateFiles(stagesDir, "*.def"))
            {
                if (!DefContentClassifier.IsValidStageDefFile(def)) continue;
                var name = Path.GetFileNameWithoutExtension(def);
                var collision = ikemenRootForCollision is null
                    ? FullgameItemCollision.New
                    : File.Exists(Path.Combine(ikemenRootForCollision, "stages", Path.GetFileName(def)))
                        ? FullgameItemCollision.Existing
                        : FullgameItemCollision.New;
                stages.Add(new FullgameStageItem(def, name, true, collision));
            }
        }

        FullgameScreenpackItem? screenpack = null;
        var systemDef = Path.Combine(dataDir, "system.def");
        if (File.Exists(systemDef) && DefContentClassifier.IsValidScreenpackDefFile(systemDef))
        {
            var parsed = DefParser.ParseFile(systemDef);
            var display = parsed?.Value("name", "info")?.Trim();
            if (string.IsNullOrWhiteSpace(display))
                display = CollectionNameResolver.DeriveNameFromFolder(Path.GetFileName(root));
            var folder = CollectionNameResolver.SanitizeForFolder(display!);
            var collision = ikemenRootForCollision is null
                ? FullgameItemCollision.New
                : Directory.Exists(Path.Combine(ikemenRootForCollision, "data", folder))
                    ? FullgameItemCollision.Existing
                    : FullgameItemCollision.New;
            screenpack = new FullgameScreenpackItem(dataDir, display!, folder, collision);
        }

        var fonts = Directory.Exists(fontDir)
            ? Directory.EnumerateFiles(fontDir, "*.fnt").ToList()
            : [];
        var sounds = Directory.Exists(soundDir)
            ? Directory.EnumerateFiles(soundDir)
                .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

        return new FullgameManifest
        {
            SourcePath = root,
            SourceFolderName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Characters = characters,
            Stages = stages,
            Screenpack = screenpack,
            Fonts = fonts,
            Sounds = sounds
        };
    }

    public FullgameImportResult Install(
        FullgameManifest manifest,
        string ikemenRoot,
        Func<string, string, FullgameDuplicateAction>? duplicateHandler = null)
    {
        var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
        RejectForbiddenContent(manifest.SourcePath);
        var result = new FullgameImportResult();
        duplicateHandler ??= (_, _) => FullgameDuplicateAction.Skip;
        var useCharSticky = false;
        var useStageSticky = false;
        FullgameDuplicateAction stickyChar = FullgameDuplicateAction.Skip;
        FullgameDuplicateAction stickyStage = FullgameDuplicateAction.Skip;

        // Characters
        foreach (var character in manifest.Characters)
        {
            try
            {
                var dest = Path.Combine(root, "chars", character.FolderName);
                var action = ResolveDuplicate(Directory.Exists(dest), character.FolderName, "character",
                    ref useCharSticky, ref stickyChar, duplicateHandler);
                if (action == FullgameDuplicateAction.Skip || action == FullgameDuplicateAction.SkipAll)
                {
                    result.CharactersSkipped.Add(character.FolderName);
                    continue;
                }

                var package = new DetectedPackage
                {
                    Kind = InstallContentKind.Character,
                    DisplayName = character.FolderName,
                    SuggestedFolderName = character.FolderName,
                    PackageRoot = character.FolderPath,
                    DefPath = character.DefPath,
                    SourceInput = manifest.SourcePath
                };
                var item = BuildDirectoryItem(package, dest, Directory.Exists(dest), action);
                var batch = _installer.Execute([item], root);
                ApplyItemResult(batch.Items[0], character.FolderName, result.CharactersInstalled, result.CharactersFailed, result.OperationIds);
            }
            catch (Exception ex)
            {
                result.CharactersFailed.Add((character.FolderName, ex.Message));
            }
        }

        // Stages
        foreach (var stage in manifest.Stages)
        {
            try
            {
                if (stage.IsLooseFile)
                {
                    var staged = RestructureLooseStage(stage);
                    try
                    {
                        InstallStageFolder(staged, stage.Name, root, result, ref useStageSticky, ref stickyStage, duplicateHandler);
                    }
                    finally
                    {
                        TryDelete(staged);
                    }
                }
                else
                {
                    InstallStageFolder(stage.Path, stage.Name, root, result, ref useStageSticky, ref stickyStage, duplicateHandler);
                }
            }
            catch (Exception ex)
            {
                result.StagesFailed.Add((stage.Name, ex.Message));
            }
        }

        // Screenpack — replace if exists (Mac behavior), still SafeMutation
        if (manifest.Screenpack is { } sp)
        {
            try
            {
                var dest = Path.Combine(root, "data", sp.SuggestedFolderName);
                var action = Directory.Exists(dest) ? FullgameDuplicateAction.Replace : FullgameDuplicateAction.Replace;
                if (Directory.Exists(dest))
                {
                    var asked = duplicateHandler(sp.DisplayName, "screenpack");
                    if (asked is FullgameDuplicateAction.Skip or FullgameDuplicateAction.SkipAll)
                    {
                        result.ScreenpackSkipped = sp.DisplayName;
                        goto Fonts;
                    }
                }

                var package = new DetectedPackage
                {
                    Kind = InstallContentKind.Screenpack,
                    DisplayName = sp.DisplayName,
                    SuggestedFolderName = sp.SuggestedFolderName,
                    PackageRoot = sp.DataFolderPath,
                    DefPath = Path.Combine(sp.DataFolderPath, "system.def"),
                    SourceInput = manifest.SourcePath,
                    Warnings = ["Screenpack installed without Motif activation."]
                };
                var item = BuildDirectoryItem(package, dest, Directory.Exists(dest),
                    Directory.Exists(dest) ? FullgameDuplicateAction.Replace : FullgameDuplicateAction.Replace);
                var batch = _installer.Execute([item], root);
                var done = batch.Items[0];
                if (done.Outcome == InstallItemOutcome.Installed)
                {
                    result.ScreenpackInstalled = sp.SuggestedFolderName;
                    if (!string.IsNullOrEmpty(done.OperationId)) result.OperationIds.Add(done.OperationId!);
                }
                else
                {
                    result.ScreenpackFailed = done.Error ?? "Screenpack install failed.";
                }
            }
            catch (Exception ex)
            {
                result.ScreenpackFailed = ex.Message;
            }
        }

        Fonts:
        InstallLooseFiles(manifest.Fonts, Path.Combine(root, "font"), result.FontsInstalled, result.FontsSkipped, result.OperationIds, root);
        InstallLooseFiles(manifest.Sounds, Path.Combine(root, "sound"), result.SoundsInstalled, result.SoundsSkipped, result.OperationIds, root);

        if (result.CharactersInstalled.Count + result.StagesInstalled.Count + (result.ScreenpackInstalled is null ? 0 : 1) > 0)
        {
            try
            {
                var collection = _collections.Create(root, manifest.SuggestedCollectionName);
                if (result.CharactersInstalled.Count > 0)
                {
                    var entries = result.CharactersInstalled.Select(name =>
                    {
                        var folder = Path.Combine(root, "chars", name);
                        var def = Directory.Exists(folder) ? FindCharacterDef(folder, name) : null;
                        var rel = def is null
                            ? $"chars/{name}/{name}.def"
                            : Path.GetRelativePath(root, def).Replace('\\', '/');
                        return new Models.CharacterEntry
                        {
                            Id = name,
                            Name = name,
                            DisplayName = name,
                            Author = "Imported",
                            VersionDate = "",
                            DefPath = rel,
                            FolderPath = $"chars/{name}"
                        };
                    });
                    collection = _collections.Add(root, collection.Id, entries);
                }

                result.CollectionCreated = collection;
            }
            catch (Exception)
            {
                // Collection failure should not undo content installs.
            }
        }

        return result;
    }

    private void InstallStageFolder(
        string folderPath,
        string name,
        string root,
        FullgameImportResult result,
        ref bool sticky,
        ref FullgameDuplicateAction stickyAction,
        Func<string, string, FullgameDuplicateAction> handler)
    {
        var dest = Path.Combine(root, "stages", name);
        var action = ResolveDuplicate(Directory.Exists(dest), name, "stage", ref sticky, ref stickyAction, handler);
        if (action is FullgameDuplicateAction.Skip or FullgameDuplicateAction.SkipAll)
        {
            result.StagesSkipped.Add(name);
            return;
        }

        var def = Directory.EnumerateFiles(folderPath, "*.def").First(f => DefContentClassifier.IsValidStageDefFile(f));
        var package = new DetectedPackage
        {
            Kind = InstallContentKind.Stage,
            DisplayName = name,
            SuggestedFolderName = name,
            PackageRoot = folderPath,
            DefPath = def,
            SourceInput = folderPath
        };
        var item = BuildDirectoryItem(package, dest, Directory.Exists(dest), action);
        var batch = _installer.Execute([item], root);
        ApplyItemResult(batch.Items[0], name, result.StagesInstalled, result.StagesFailed, result.OperationIds);
    }

    private string RestructureLooseStage(FullgameStageItem stage)
    {
        var temp = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"), stage.Name);
        Directory.CreateDirectory(temp);
        var defName = Path.GetFileName(stage.Path);
        File.Copy(stage.Path, Path.Combine(temp, defName), true);
        var stem = Path.GetFileNameWithoutExtension(stage.Path);
        var dir = Path.GetDirectoryName(stage.Path)!;
        foreach (var sff in Directory.EnumerateFiles(dir, stem + "*.sff"))
            File.Copy(sff, Path.Combine(temp, Path.GetFileName(sff)), true);
        return temp;
    }

    private void InstallLooseFiles(
        IReadOnlyList<string> sources,
        string destDir,
        List<string> installed,
        List<string> skipped,
        List<string> ops,
        string ikemenRoot)
    {
        if (sources.Count == 0) return;
        Directory.CreateDirectory(destDir);
        foreach (var src in sources)
        {
            var name = Path.GetFileName(src);
            var dest = Path.Combine(destDir, name);
            if (File.Exists(dest))
            {
                skipped.Add(name);
                continue;
            }

            try
            {
                var plan = _mutations.PlanCreateFile(ikemenRoot, dest, File.ReadAllBytes(src));
                if (!plan.IsValid)
                {
                    skipped.Add(name);
                    continue;
                }

                var mutation = _mutations.CreateFile(ikemenRoot, dest, File.ReadAllBytes(src));
                if (mutation.Success)
                {
                    installed.Add(name);
                    if (!string.IsNullOrEmpty(mutation.OperationId)) ops.Add(mutation.OperationId);
                }
                else skipped.Add(name);
            }
            catch
            {
                skipped.Add(name);
            }
        }
    }

    private static InstallPlanItem BuildDirectoryItem(
        DetectedPackage package,
        string dest,
        bool exists,
        FullgameDuplicateAction action)
    {
        return new InstallPlanItem
        {
            Package = package,
            TargetDirectory = dest,
            DestinationExists = exists,
            Decision = exists
                ? (action is FullgameDuplicateAction.Replace or FullgameDuplicateAction.ReplaceAll
                    ? InstallItemDecision.Replace
                    : InstallItemDecision.Skip)
                : InstallItemDecision.InstallNew
        };
    }

    private static void ApplyItemResult(
        InstallPlanItem item,
        string name,
        List<string> installed,
        List<(string, string)> failed,
        List<string> ops)
    {
        if (item.Outcome == InstallItemOutcome.Installed)
        {
            installed.Add(name);
            if (!string.IsNullOrEmpty(item.OperationId)) ops.Add(item.OperationId!);
        }
        else if (item.Outcome != InstallItemOutcome.Skipped)
        {
            failed.Add((name, item.Error ?? item.Outcome.ToString()));
        }
    }

    private static FullgameDuplicateAction ResolveDuplicate(
        bool exists,
        string name,
        string type,
        ref bool sticky,
        ref FullgameDuplicateAction stickyAction,
        Func<string, string, FullgameDuplicateAction> handler)
    {
        if (!exists) return FullgameDuplicateAction.Replace;
        if (sticky) return stickyAction;
        var action = handler(name, type);
        if (action is FullgameDuplicateAction.ReplaceAll or FullgameDuplicateAction.SkipAll)
        {
            sticky = true;
            stickyAction = action == FullgameDuplicateAction.ReplaceAll
                ? FullgameDuplicateAction.Replace
                : FullgameDuplicateAction.Skip;
            return action == FullgameDuplicateAction.ReplaceAll
                ? FullgameDuplicateAction.Replace
                : FullgameDuplicateAction.Skip;
        }

        return action;
    }

    /// <summary>
    /// The character folder keeps its name; which DEF labels it comes from the shared resolver (an
    /// ambiguous folder is still imported, and the library asks which DEF to use when it is enabled).
    /// </summary>
    private static string? FindCharacterDef(string folder, string folderName)
        => PrimaryDefResolver.Resolve(folder, folderName).DisplayCandidate?.FullPath;

    private static void RejectForbiddenContent(string packageRoot)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(packageRoot))
        {
            var name = Path.GetFileName(entry);
            if (ForbiddenNames.Contains(name) ||
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // Presence of engine binaries is OK in a fullgame dump; we simply never copy them.
                // Only fail if someone tries to pass the IKEMEN root itself as the package.
                continue;
            }
        }

        // Hard reject: package path IS an IKEMEN engine root (has Ikemen_GO.exe + chars + data).
        if (File.Exists(Path.Combine(packageRoot, "Ikemen_GO.exe")) &&
            Directory.Exists(Path.Combine(packageRoot, "external")))
        {
            throw new InvalidOperationException(
                "Refusing to import an IKEMEN engine root as a fullgame package.");
        }
    }

    private static bool IsJunk(string? name)
        => string.IsNullOrWhiteSpace(name) || name.StartsWith('.') ||
           name.Equals("template", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("readme", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* ignore */ }
    }
}
