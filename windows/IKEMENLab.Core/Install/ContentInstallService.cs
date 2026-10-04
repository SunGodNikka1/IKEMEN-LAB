using IKEMENLab.Core.Library;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Install;

public interface IContentInstallService
{
    InspectBatchResult Inspect(IEnumerable<string> inputs, string ikemenRoot, string? stagingRoot = null);
    InstallBatchResult Execute(IReadOnlyList<InstallPlanItem> items, string ikemenRoot, bool dryRun = false);
    void CleanupStaging(IEnumerable<string> stagingDirectories);
}

/// <summary>
/// Character + stage installer. Live IKEMEN writes go exclusively through ISafeMutationService.
/// Does not modify select.def or config.ini.
/// </summary>
public sealed class ContentInstallService : IContentInstallService
{
    private readonly ISafeMutationService _mutations;
    private readonly DateAddedTracker _dateAdded;
    private readonly PrimaryDefStore? _primaryDefs;

    /// <param name="dateAdded">
    /// Records the exact Date Added of successful installs. Omit only for tests/tools; the app passes
    /// the same tracker the library index uses.
    /// </param>
    /// <param name="primaryDefs">Where an explicit DEF choice is saved (app data, never the IKEMEN root).</param>
    public ContentInstallService(ISafeMutationService? mutations = null, DateAddedTracker? dateAdded = null,
        PrimaryDefStore? primaryDefs = null)
    {
        _mutations = mutations ?? new SafeMutationService();
        _dateAdded = dateAdded ?? DateAddedTracker.EstimateOnly;
        _primaryDefs = primaryDefs;
    }

    public InspectBatchResult Inspect(IEnumerable<string> inputs, string ikemenRoot, string? stagingRoot = null)
    {
        var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
        var items = new List<InstallPlanItem>();
        var failures = new List<InspectFailure>();
        var stagingDirs = new List<string>();
        var stagingBase = stagingRoot ?? ArchiveExtractor.GetDefaultStagingRoot();

        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input)) continue;
            try
            {
                var full = Path.GetFullPath(input);
                if (Directory.Exists(full))
                {
                    AddPackagesFromDirectory(full, full, root, items);
                }
                else if (File.Exists(full))
                {
                    if (!ArchiveExtractor.IsArchiveFile(full) &&
                        ArchiveExtractor.DetectFormat(full) is null)
                    {
                        failures.Add(new InspectFailure
                        {
                            SourceInput = full,
                            Reason = "Not a supported archive (.zip, .rar, .7z) or folder."
                        });
                        continue;
                    }

                    if (ArchiveExtractor.DetectFormat(full) == "ace")
                    {
                        failures.Add(new InspectFailure
                        {
                            SourceInput = full,
                            Reason = "ACE archives are not supported."
                        });
                        continue;
                    }

                    var staging = ArchiveExtractor.ExtractToStaging(full, stagingBase);
                    stagingDirs.Add(staging);
                    AddPackagesFromDirectory(staging, full, root, items);
                }
                else
                {
                    failures.Add(new InspectFailure
                    {
                        SourceInput = input,
                        Reason = "Path does not exist."
                    });
                }
            }
            catch (Exception ex)
            {
                failures.Add(new InspectFailure
                {
                    SourceInput = input,
                    Reason = ex.Message
                });
            }
        }

        InstallPlanRules.Refresh(items, root);
        return new InspectBatchResult
        {
            Items = items,
            Failures = failures,
            StagingDirectories = stagingDirs
        };
    }

    private void AddPackagesFromDirectory(
        string directory,
        string sourceInput,
        string ikemenRoot,
        List<InstallPlanItem> items)
    {
        IReadOnlyList<DetectedPackage> packages;
        try
        {
            packages = ContentDetector.DetectFromDirectory(directory, sourceInput);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        foreach (var package in packages)
        {
            items.Add(BuildPlanItem(package, ikemenRoot, directory));
        }
    }

    private InstallPlanItem BuildPlanItem(DetectedPackage package, string ikemenRoot, string scanRoot)
    {
        if (package.Kind == InstallContentKind.Character)
        {
            var target = Path.Combine(ikemenRoot, "chars", package.SuggestedFolderName);
            var exists = Directory.Exists(target);
            var plan = exists
                ? _mutations.PlanReplaceDirectory(ikemenRoot, target, package.PackageRoot)
                : _mutations.PlanCreateDirectory(ikemenRoot, target, package.PackageRoot);

            return new InstallPlanItem
            {
                Package = package,
                TargetDirectory = target,
                DestinationExists = exists,
                Decision = exists ? InstallItemDecision.NeedsDecision : InstallItemDecision.InstallNew,
                MutationPlan = plan.IsValid || exists ? plan : plan,
                AffectedPaths = plan.AffectedPaths
            };
        }

        if (package.Kind == InstallContentKind.Screenpack)
        {
            var target = Path.Combine(ikemenRoot, "data", package.SuggestedFolderName);
            var exists = Directory.Exists(target);
            var warnings = new List<string>(package.Warnings);
            if (!warnings.Any(w => w.Contains("activate", StringComparison.OrdinalIgnoreCase)))
                warnings.Add("Screenpack will not be activated automatically after install.");
            var enriched = new DetectedPackage
            {
                Kind = package.Kind,
                DisplayName = package.DisplayName,
                SuggestedFolderName = package.SuggestedFolderName,
                PackageRoot = package.PackageRoot,
                DefPath = package.DefPath,
                SourceInput = package.SourceInput,
                Warnings = warnings,
                MissingAssets = package.MissingAssets,
                FileCount = package.FileCount
            };
            var plan = exists
                ? _mutations.PlanReplaceDirectory(ikemenRoot, target, package.PackageRoot)
                : _mutations.PlanCreateDirectory(ikemenRoot, target, package.PackageRoot);

            return new InstallPlanItem
            {
                Package = enriched,
                TargetDirectory = target,
                DestinationExists = exists,
                Decision = exists ? InstallItemDecision.NeedsDecision : InstallItemDecision.InstallNew,
                MutationPlan = plan,
                AffectedPaths = plan.AffectedPaths
            };
        }

        // Stage
        var flat = ContentDetector.IsFlatStageLayout(package, scanRoot);
        var stageDestination = flat ? "stages" : "stages/" + package.SuggestedFolderName;
        var assets = PlanStageAssets(package, stageDestination, scanRoot, ikemenRoot);
        var stageWarnings = new List<string>(package.Warnings);
        if (flat) stageWarnings.Add("Flat stage layout: files will install directly under stages/.");
        else if (File.Exists(package.SourceInput) &&
                 string.Equals(Path.TrimEndingDirectorySeparator(package.PackageRoot), Path.TrimEndingDirectorySeparator(scanRoot), StringComparison.OrdinalIgnoreCase))
            stageWarnings.Add($"The archive has no stage folder, so it installs as stages/{package.SuggestedFolderName}/ (named after the archive).");
        foreach (var companion in assets.Companions)
            stageWarnings.Add($"Also installs {companion.Destination} (referenced as {companion.Reference}).");
        stageWarnings.AddRange(assets.Warnings);

        var stagePackage = new DetectedPackage
        {
            Kind = package.Kind,
            DisplayName = package.DisplayName,
            SuggestedFolderName = package.SuggestedFolderName,
            PackageRoot = package.PackageRoot,
            DefPath = package.DefPath,
            SourceInput = package.SourceInput,
            Warnings = stageWarnings,
            MissingAssets = assets.Missing,
            FileCount = package.FileCount + assets.Companions.Count,
            Companions = assets.Companions,
            IsFlatStage = flat
        };
        var companionPaths = assets.Companions
            .Select(c => Path.Combine(ikemenRoot, c.Destination.Replace('/', Path.DirectorySeparatorChar)))
            .ToList();

        if (flat)
        {
            var stagesRoot = Path.Combine(ikemenRoot, "stages");
            var flatPaths = FlatStageFiles(package.PackageRoot)
                .Select(f => Path.Combine(stagesRoot, Path.GetRelativePath(package.PackageRoot, f)))
                .ToList();
            var anyExists = flatPaths.Any(File.Exists);

            return new InstallPlanItem
            {
                Package = stagePackage,
                TargetDirectory = stagesRoot,
                DestinationExists = anyExists,
                Decision = anyExists ? InstallItemDecision.NeedsDecision : InstallItemDecision.InstallNew,
                AffectedPaths = flatPaths.Concat(companionPaths).ToList()
            };
        }

        {
            var target = Path.Combine(ikemenRoot, "stages", package.SuggestedFolderName);
            var exists = Directory.Exists(target);
            var plan = exists
                ? _mutations.PlanReplaceDirectory(ikemenRoot, target, package.PackageRoot)
                : _mutations.PlanCreateDirectory(ikemenRoot, target, package.PackageRoot);

            return new InstallPlanItem
            {
                Package = stagePackage,
                TargetDirectory = target,
                DestinationExists = exists,
                Decision = exists ? InstallItemDecision.NeedsDecision : InstallItemDecision.InstallNew,
                MutationPlan = plan,
                AffectedPaths = plan.AffectedPaths.Concat(companionPaths).ToList()
            };
        }
    }

    /// <summary>Resolves what every stage DEF in the package references, per DEF folder, against the archive and the install.</summary>
    private static StageAssetPlan PlanStageAssets(DetectedPackage package, string stageDestination, string scanRoot, string ikemenRoot)
    {
        var companions = new Dictionary<string, StageCompanion>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var warnings = new List<string>();
        var defs = Directory.EnumerateFiles(package.PackageRoot, "*.def", SearchOption.AllDirectories)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && DefContentClassifier.IsValidStageDefFile(d))
            .GroupBy(d => Path.GetDirectoryName(d)!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in defs)
        {
            var relDir = Path.GetRelativePath(package.PackageRoot, group.Key).Replace('\\', '/');
            var destination = relDir == "." ? stageDestination : stageDestination + "/" + relDir;
            var plan = StageAssetResolver.Resolve(group.ToList(), group.Key, scanRoot, ikemenRoot, destination);
            foreach (var c in plan.Companions) companions[c.Destination] = c;
            missing.AddRange(plan.Missing);
            warnings.AddRange(plan.Warnings);
        }

        return new StageAssetPlan
        {
            Companions = companions.Values.ToList(),
            Missing = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Warnings = warnings.Distinct().ToList()
        };
    }

    /// <summary>Every file a loose stage layout installs, preserving its subfolders.</summary>
    private static List<string> FlatStageFiles(string packageRoot)
    {
        return Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Where(f => !IsIgnoredFile(f) && !Path.GetRelativePath(packageRoot, f)
                .Split(Path.DirectorySeparatorChar)
                .SkipLast(1)
                .Any(part => part.StartsWith('.') || part.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public InstallBatchResult Execute(IReadOnlyList<InstallPlanItem> items, string ikemenRoot, bool dryRun = false)
    {
        var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
        InstallPlanRules.Refresh(items, root);

        foreach (var item in items)
        {
            try
            {
                if (item.Decision == InstallItemDecision.Skip)
                {
                    item.Outcome = InstallItemOutcome.Skipped;
                    continue;
                }

                if (item.Problem is { } problem)
                {
                    item.Outcome = InstallItemOutcome.Rejected;
                    item.Error = problem;
                    continue;
                }

                if (item.NeedsDefChoice)
                {
                    item.Outcome = InstallItemOutcome.Rejected;
                    item.Error = "This package has several possible DEFs. Choose which one the character uses.";
                    continue;
                }

                if (item.NeedsLayoutConfirmation)
                {
                    item.Outcome = InstallItemOutcome.Rejected;
                    item.Error = "Files outside the character folder would not be installed. Confirm to install without them.";
                    continue;
                }

                if (item.SelectedDef is { } selected &&
                    !item.Package.DefCandidates.Any(c => string.Equals(c.RelativePath, selected, StringComparison.OrdinalIgnoreCase)))
                {
                    item.Outcome = InstallItemOutcome.Rejected;
                    item.Error = $"'{selected}' is not one of this package's DEFs.";
                    continue;
                }

                if (item.Decision == InstallItemDecision.NeedsDecision)
                {
                    item.Outcome = InstallItemOutcome.Rejected;
                    item.Error = "Duplicate destination requires an explicit Replace or Skip decision.";
                    continue;
                }

                var flatStage = item.Package.Kind == InstallContentKind.Stage &&
                    string.Equals(
                        Path.GetFullPath(item.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(Path.Combine(root, "stages")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);

                // A replacement recreates the folder/files; make sure the existing item has a Date Added
                // record first so the update keeps its original date.
                if (!dryRun && item.Decision == InstallItemDecision.Replace)
                {
                    _dateAdded.EnsureRecorded(root, DateAddedIdentities(item, root, flatStage));
                }

                if (flatStage)
                {
                    ExecuteFlatStage(item, root, dryRun);
                }
                else
                {
                    ExecuteDirectoryPackage(item, root, dryRun);
                }

                // Only a completed install establishes a Date Added (never failed/skipped/dry-run items).
                if (!dryRun && item.Outcome == InstallItemOutcome.Installed)
                {
                    _dateAdded.RecordInstalled(root, DateAddedIdentities(item, root, flatStage));
                    if (item.Package.Kind == InstallContentKind.Character) SaveDefChoice(item, root);
                }
            }
            catch (Exception ex)
            {
                item.Outcome = InstallItemOutcome.Failed;
                item.Error = ex.Message;
            }
        }

        return new InstallBatchResult { Items = items };
    }

    private void ExecuteDirectoryPackage(InstallPlanItem item, string ikemenRoot, bool dryRun)
    {
        MutationResult result;
        if (item.Decision == InstallItemDecision.Replace || item.DestinationExists)
        {
            if (item.Decision != InstallItemDecision.Replace)
            {
                item.Outcome = InstallItemOutcome.Rejected;
                item.Error = "Replace was not confirmed.";
                return;
            }

            var plan = _mutations.PlanReplaceDirectory(ikemenRoot, item.TargetDirectory, item.Package.PackageRoot);
            item.MutationPlan = plan;
            if (!plan.IsValid)
            {
                item.Outcome = InstallItemOutcome.Failed;
                item.Error = plan.RejectionReason ?? "Invalid replace plan.";
                return;
            }

            if (dryRun)
            {
                item.Outcome = InstallItemOutcome.Installed;
                item.Error = null;
                return;
            }

            result = _mutations.ReplaceDirectory(ikemenRoot, item.TargetDirectory, item.Package.PackageRoot);
        }
        else
        {
            var plan = _mutations.PlanCreateDirectory(ikemenRoot, item.TargetDirectory, item.Package.PackageRoot);
            item.MutationPlan = plan;
            if (!plan.IsValid)
            {
                item.Outcome = InstallItemOutcome.Failed;
                item.Error = plan.RejectionReason ?? "Invalid create plan.";
                return;
            }

            if (dryRun)
            {
                item.Outcome = InstallItemOutcome.Installed;
                return;
            }

            result = _mutations.CreateDirectory(ikemenRoot, item.TargetDirectory, item.Package.PackageRoot);
        }

        item.OperationId = result.OperationId;
        if (result.Success)
        {
            var opIds = new List<string> { result.OperationId };
            if (InstallCompanions(item, ikemenRoot, opIds)) item.Outcome = InstallItemOutcome.Installed;
        }
        else
        {
            item.Outcome = InstallItemOutcome.Failed;
            item.Error = result.Error ?? "Mutation failed.";
            // SafeMutationService rolls back on failure; destination should be preserved.
        }
    }

    /// <summary>
    /// Writes the files the DEF references from outside the stage folder. On any failure everything this item
    /// wrote (including the stage folder, via <paramref name="opIds"/>) is rolled back.
    /// </summary>
    private bool InstallCompanions(InstallPlanItem item, string ikemenRoot, List<string> opIds)
    {
        foreach (var companion in item.Package.Companions)
        {
            var dest = Path.Combine(ikemenRoot, companion.Destination.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest))
            {
                if (File.ReadAllBytes(dest).AsSpan().SequenceEqual(File.ReadAllBytes(companion.SourcePath))) continue;
                RollbackOps(opIds);
                item.Outcome = InstallItemOutcome.Failed;
                item.Error = $"{companion.Destination} already exists and is different; nothing was installed.";
                return false;
            }

            var result = _mutations.CreateFile(ikemenRoot, dest, File.ReadAllBytes(companion.SourcePath));
            opIds.Add(result.OperationId);
            if (!result.Success)
            {
                RollbackOps(opIds);
                item.Outcome = InstallItemOutcome.Failed;
                item.Error = result.Error ?? $"Could not install {companion.Destination}.";
                return false;
            }
        }

        item.OperationId = opIds.LastOrDefault();
        return true;
    }

    private void ExecuteFlatStage(InstallPlanItem item, string ikemenRoot, bool dryRun)
    {
        var stagesRoot = Path.Combine(ikemenRoot, "stages");
        var files = FlatStageFiles(item.Package.PackageRoot)
            .Select(f => (Source: f, Dest: Path.Combine(stagesRoot, Path.GetRelativePath(item.Package.PackageRoot, f))))
            .Concat(item.Package.Companions.Select(c =>
                (Source: c.SourcePath, Dest: Path.Combine(ikemenRoot, c.Destination.Replace('/', Path.DirectorySeparatorChar)))))
            .ToList();

        if (files.Count == 0)
        {
            item.Outcome = InstallItemOutcome.Failed;
            item.Error = "No stage files to install.";
            return;
        }

        if (dryRun)
        {
            // Preview only — plan each file, mutate nothing.
            foreach (var (file, dest) in files)
            {
                var plan = File.Exists(dest)
                    ? _mutations.PlanReplaceFile(ikemenRoot, dest, file)
                    : _mutations.PlanCreateFile(ikemenRoot, dest, Array.Empty<byte>());
                if (!plan.IsValid && item.Decision != InstallItemDecision.Replace)
                {
                    item.Outcome = InstallItemOutcome.Failed;
                    item.Error = plan.RejectionReason;
                    return;
                }
            }

            item.Outcome = InstallItemOutcome.Installed;
            return;
        }

        var opIds = new List<string>();
        try
        {
            foreach (var (file, dest) in files)
            {
                MutationResult result;
                if (File.Exists(dest))
                {
                    if (item.Decision != InstallItemDecision.Replace)
                    {
                        item.Outcome = InstallItemOutcome.Rejected;
                        item.Error = "Duplicate stage file requires Replace.";
                        RollbackOps(opIds);
                        return;
                    }

                    result = _mutations.ReplaceFile(ikemenRoot, dest, file);
                }
                else
                {
                    result = _mutations.CreateFile(ikemenRoot, dest, File.ReadAllBytes(file));
                }

                opIds.Add(result.OperationId);
                if (!result.Success)
                {
                    item.Outcome = InstallItemOutcome.Failed;
                    item.Error = result.Error ?? "Flat stage file mutation failed.";
                    RollbackOps(opIds);
                    return;
                }
            }

            item.OperationId = opIds.LastOrDefault();
            item.Outcome = InstallItemOutcome.Installed;
        }
        catch (Exception ex)
        {
            RollbackOps(opIds);
            item.Outcome = InstallItemOutcome.Failed;
            item.Error = ex.Message;
        }
    }

    private void RollbackOps(IEnumerable<string> operationIds)
    {
        foreach (var id in operationIds.Reverse())
        {
            try { _mutations.Rollback(id); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Saves the DEF the user picked when the resolver alone would not have chosen it (ambiguous package or
    /// a different DEF). A saved choice the installed files no longer contain is dropped.
    /// </summary>
    private void SaveDefChoice(InstallPlanItem item, string root)
    {
        if (_primaryDefs is null) return;
        var folder = item.DestinationName;
        try
        {
            if (item.SelectedDef is { } selected &&
                (item.Package.RequiresDefChoice ||
                 !string.Equals(selected, item.Package.PrimaryDef?.RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                _primaryDefs.Set(root, folder, selected);
                return;
            }

            var saved = _primaryDefs.Get(root, folder);
            if (saved is not null && !File.Exists(Path.Combine(item.TargetDirectory, saved.Replace('/', Path.DirectorySeparatorChar))))
                _primaryDefs.Remove(root, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.Error = "Installed, but the DEF choice could not be saved: " + ex.Message;
        }
    }

    /// <summary>Library identities an install item creates or updates (none for screenpacks).</summary>
    private static IEnumerable<string> DateAddedIdentities(InstallPlanItem item, string root, bool flatStage)
    {
        switch (item.Package.Kind)
        {
            case InstallContentKind.Character:
                return [ContentIdentity.ForCharacterFolder(Path.GetFileName(
                    item.TargetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))];

            case InstallContentKind.Stage when flatStage:
                return FlatStageFiles(item.Package.PackageRoot)
                    .Where(f => f.EndsWith(".def", StringComparison.OrdinalIgnoreCase))
                    .Select(f => ContentIdentity.ForStageDef(Path.GetRelativePath(item.Package.PackageRoot, f).Replace('\\', '/')))
                    .ToList();

            case InstallContentKind.Stage:
            {
                var folder = Path.GetFileName(item.TargetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var source = Directory.Exists(item.TargetDirectory) ? item.TargetDirectory : item.Package.PackageRoot;
                return Directory.EnumerateFiles(source, "*.def")
                    .Select(f => ContentIdentity.ForStageDef(folder + "/" + Path.GetFileName(f)))
                    .ToList();
            }

            default:
                return [];
        }
    }

    public void CleanupStaging(IEnumerable<string> stagingDirectories)
    {
        foreach (var dir in stagingDirectories)
            ArchiveExtractor.TryDeleteDirectory(dir);
    }

    private static bool IsIgnoredFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.') ||
               name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }
}
