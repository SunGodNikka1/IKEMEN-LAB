using IKEMENLab.Core.Mutations;
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

    public ContentInstallService(ISafeMutationService? mutations = null)
    {
        _mutations = mutations ?? new SafeMutationService();
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

        // Stage
        var flat = ContentDetector.IsFlatStageLayout(package, scanRoot);
        if (flat)
        {
            var stagesRoot = Path.Combine(ikemenRoot, "stages");
            var files = Directory.EnumerateFiles(package.PackageRoot)
                .Where(f => !IsIgnoredFile(f))
                .ToList();
            var anyExists = files.Any(f => File.Exists(Path.Combine(stagesRoot, Path.GetFileName(f))));
            var warnings = new List<string>(package.Warnings)
            {
                "Flat stage layout: files will install directly under stages/."
            };
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

            return new InstallPlanItem
            {
                Package = enriched,
                TargetDirectory = stagesRoot,
                DestinationExists = anyExists,
                Decision = anyExists ? InstallItemDecision.NeedsDecision : InstallItemDecision.InstallNew,
                AffectedPaths = files.Select(f => Path.Combine(stagesRoot, Path.GetFileName(f))).ToList()
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
                Package = package,
                TargetDirectory = target,
                DestinationExists = exists,
                Decision = exists ? InstallItemDecision.NeedsDecision : InstallItemDecision.InstallNew,
                MutationPlan = plan,
                AffectedPaths = plan.AffectedPaths
            };
        }
    }

    public InstallBatchResult Execute(IReadOnlyList<InstallPlanItem> items, string ikemenRoot, bool dryRun = false)
    {
        var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);

        foreach (var item in items)
        {
            try
            {
                if (item.Decision == InstallItemDecision.Skip)
                {
                    item.Outcome = InstallItemOutcome.Skipped;
                    continue;
                }

                if (item.Decision == InstallItemDecision.NeedsDecision)
                {
                    item.Outcome = InstallItemOutcome.Rejected;
                    item.Error = "Duplicate destination requires an explicit Replace or Skip decision.";
                    continue;
                }

                if (item.Package.Kind == InstallContentKind.Stage &&
                    string.Equals(
                        Path.GetFullPath(item.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(Path.Combine(root, "stages")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteFlatStage(item, root, dryRun);
                    continue;
                }

                ExecuteDirectoryPackage(item, root, dryRun);
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
            item.Outcome = InstallItemOutcome.Installed;
        }
        else
        {
            item.Outcome = InstallItemOutcome.Failed;
            item.Error = result.Error ?? "Mutation failed.";
            // SafeMutationService rolls back on failure; destination should be preserved.
        }
    }

    private void ExecuteFlatStage(InstallPlanItem item, string ikemenRoot, bool dryRun)
    {
        var stagesRoot = Path.Combine(ikemenRoot, "stages");
        var files = Directory.EnumerateFiles(item.Package.PackageRoot)
            .Where(f => !IsIgnoredFile(f))
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
            foreach (var file in files)
            {
                var dest = Path.Combine(stagesRoot, Path.GetFileName(file));
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
            foreach (var file in files)
            {
                var dest = Path.Combine(stagesRoot, Path.GetFileName(file));
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
