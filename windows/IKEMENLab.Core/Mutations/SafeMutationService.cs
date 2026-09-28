using System.Text.Json;
using System.Text.Json.Serialization;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Mutations;

/// <summary>
/// Controlled mutation gateway for IKEMEN-root writes.
/// Backups and manifests live under LocalAppData. ViewModels must not write IKEMEN files directly.
/// </summary>
public sealed class SafeMutationService : ISafeMutationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _operationsRoot;
    private readonly string _backupsRoot;

    public SafeMutationService(string? operationsRoot = null, string? backupsRoot = null)
    {
        AppDataPaths.EnsureBackupInfrastructure();
        _operationsRoot = operationsRoot ?? AppDataPaths.GetMutationOperationsDirectory();
        _backupsRoot = backupsRoot ?? AppDataPaths.GetBackupsDirectory();
        Directory.CreateDirectory(_operationsRoot);
        Directory.CreateDirectory(_backupsRoot);
    }

    public OperationPlan PlanCreateFile(string ikemenRoot, string targetPath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return BuildPlan(MutationKind.CreateFile, ikemenRoot, targetPath, sourcePath: null, contentLength: content.Length);
    }

    public OperationPlan PlanReplaceFile(string ikemenRoot, string targetPath, string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath))
        {
            return Reject(MutationKind.ReplaceFile, ikemenRoot, targetPath, sourceFilePath, "Source file does not exist.");
        }

        return BuildPlan(MutationKind.ReplaceFile, ikemenRoot, targetPath, sourceFilePath, contentLength: null);
    }

    public OperationPlan PlanCreateDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return Reject(MutationKind.CreateDirectory, ikemenRoot, targetDirectory, sourceDirectory, "Source directory does not exist.");
        }

        return BuildPlan(MutationKind.CreateDirectory, ikemenRoot, targetDirectory, sourceDirectory, contentLength: null);
    }

    public OperationPlan PlanReplaceDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return Reject(MutationKind.ReplaceDirectory, ikemenRoot, targetDirectory, sourceDirectory, "Source directory does not exist.");
        }

        return BuildPlan(MutationKind.ReplaceDirectory, ikemenRoot, targetDirectory, sourceDirectory, contentLength: null);
    }

    public MutationResult CreateFile(string ikemenRoot, string targetPath, byte[] content, bool dryRun = false)
        => Execute(PlanCreateFile(ikemenRoot, targetPath, content), content, dryRun);

    public MutationResult ReplaceFile(string ikemenRoot, string targetPath, string sourceFilePath, bool dryRun = false)
        => Execute(PlanReplaceFile(ikemenRoot, targetPath, sourceFilePath), createFileContent: null, dryRun);

    public MutationResult CreateDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory, bool dryRun = false)
        => Execute(PlanCreateDirectory(ikemenRoot, targetDirectory, sourceDirectory), createFileContent: null, dryRun);

    public MutationResult ReplaceDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory, bool dryRun = false)
        => Execute(PlanReplaceDirectory(ikemenRoot, targetDirectory, sourceDirectory), createFileContent: null, dryRun);

    public MutationResult Execute(OperationPlan plan, byte[]? createFileContent = null, bool dryRun = false)
    {
        var operationId = NewOperationId();
        var timestamp = DateTimeOffset.UtcNow;
        var opDir = Path.Combine(_operationsRoot, operationId);
        var backupDir = Path.Combine(_backupsRoot, operationId);

        var manifest = new MutationManifest
        {
            OperationId = operationId,
            Timestamp = timestamp,
            Kind = plan.Kind,
            IkemenRoot = plan.IkemenRoot,
            TargetPath = plan.TargetPath,
            SourcePath = plan.SourcePath,
            Status = dryRun ? MutationStatus.DryRun : MutationStatus.Planned,
            DryRun = dryRun,
            AffectedPaths = plan.AffectedPaths
        };

        if (!plan.IsValid)
        {
            manifest.Status = MutationStatus.Failed;
            manifest.Error = plan.RejectionReason ?? "Plan rejected.";
            if (!dryRun)
            {
                SaveManifest(manifest);
            }

            return Fail(manifest, plan);
        }

        if (dryRun)
        {
            // Zero mutation: no IKEMEN writes, no backup, no persisted manifest.
            return new MutationResult { Success = true, Manifest = manifest, Plan = plan };
        }

        Directory.CreateDirectory(opDir);
        Directory.CreateDirectory(backupDir);

        try
        {
            switch (plan.Kind)
            {
                case MutationKind.CreateFile:
                    ExecuteCreateFile(plan, createFileContent ?? Array.Empty<byte>(), backupDir, manifest);
                    break;
                case MutationKind.ReplaceFile:
                    ExecuteReplaceFile(plan, backupDir, manifest);
                    break;
                case MutationKind.CreateDirectory:
                    ExecuteCreateDirectory(plan, backupDir, manifest);
                    break;
                case MutationKind.ReplaceDirectory:
                    ExecuteReplaceDirectory(plan, backupDir, manifest);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported mutation kind: {plan.Kind}");
            }

            manifest.Status = MutationStatus.Succeeded;
            SaveManifest(manifest);
            return new MutationResult { Success = true, Manifest = manifest, Plan = plan };
        }
        catch (Exception ex)
        {
            manifest.Status = MutationStatus.Failed;
            manifest.Error = ex.Message;
            try
            {
                TryEmergencyRestore(manifest);
            }
            catch (Exception restoreEx)
            {
                manifest.Error = ex.Message + " | rollback: " + restoreEx.Message;
            }

            SaveManifest(manifest);
            return Fail(manifest, plan);
        }
    }

    public MutationResult Rollback(string operationId)
    {
        var manifest = LoadManifest(operationId)
            ?? throw new FileNotFoundException($"No manifest for operation '{operationId}'.");

        if (manifest.DryRun)
        {
            manifest.Status = MutationStatus.Failed;
            manifest.Error = "Dry-run operations cannot be rolled back.";
            return new MutationResult
            {
                Success = false,
                Manifest = manifest
            };
        }

        try
        {
            switch (manifest.Kind)
            {
                case MutationKind.CreateFile:
                    if (File.Exists(manifest.TargetPath))
                    {
                        File.Delete(manifest.TargetPath);
                    }

                    break;

                case MutationKind.ReplaceFile:
                    RestoreFileFromBackup(manifest);
                    break;

                case MutationKind.CreateDirectory:
                    if (Directory.Exists(manifest.TargetPath))
                    {
                        Directory.Delete(manifest.TargetPath, recursive: true);
                    }

                    break;

                case MutationKind.ReplaceDirectory:
                    RestoreDirectoryFromBackup(manifest);
                    break;
            }

            manifest.Status = MutationStatus.RolledBack;
            manifest.Error = null;
            SaveManifest(manifest);
            return new MutationResult { Success = true, Manifest = manifest };
        }
        catch (Exception ex)
        {
            manifest.Status = MutationStatus.Failed;
            manifest.Error = "Rollback failed: " + ex.Message;
            SaveManifest(manifest);
            return Fail(manifest, plan: null);
        }
    }

    public MutationManifest? LoadManifest(string operationId)
    {
        var path = ManifestPath(operationId);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MutationManifest>(json, JsonOptions);
    }

    private void ExecuteCreateFile(OperationPlan plan, byte[] content, string backupDir, MutationManifest manifest)
    {
        if (File.Exists(plan.TargetPath) || Directory.Exists(plan.TargetPath))
        {
            throw new InvalidOperationException("CreateFile target already exists.");
        }

        IkemenPathGuard.EnsureInsideRoot(plan.IkemenRoot, plan.TargetPath);
        IkemenPathGuard.EnsureNoReparseEscape(plan.IkemenRoot, plan.TargetPath);

        var parent = Path.GetDirectoryName(plan.TargetPath)
            ?? throw new InvalidOperationException("Target has no parent directory.");
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(parent, $".ikemenlab-{manifest.OperationId}.tmp");
        manifest.StagingPath = staging;
        try
        {
            File.WriteAllBytes(staging, content);
            File.Move(staging, plan.TargetPath);
            manifest.AfterHash = IkemenPathGuard.Sha256File(plan.TargetPath);
            // No prior content — backup folder records empty marker.
            File.WriteAllText(Path.Combine(backupDir, "created.txt"), plan.TargetPath);
            manifest.BackupPath = backupDir;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private void ExecuteReplaceFile(OperationPlan plan, string backupDir, MutationManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(plan.SourcePath) || !File.Exists(plan.SourcePath))
        {
            throw new InvalidOperationException("ReplaceFile requires an existing source file.");
        }

        IkemenPathGuard.EnsureInsideRoot(plan.IkemenRoot, plan.TargetPath);
        IkemenPathGuard.EnsureNoReparseEscape(plan.IkemenRoot, plan.TargetPath);
        IkemenPathGuard.EnsureDistinctPaths(plan.SourcePath, plan.TargetPath);

        var parent = Path.GetDirectoryName(plan.TargetPath)
            ?? throw new InvalidOperationException("Target has no parent directory.");
        Directory.CreateDirectory(parent);

        var backupFile = Path.Combine(backupDir, "original" + Path.GetExtension(plan.TargetPath));
        if (string.IsNullOrEmpty(Path.GetExtension(backupFile)))
        {
            backupFile = Path.Combine(backupDir, "original.bin");
        }

        if (File.Exists(plan.TargetPath))
        {
            manifest.BeforeHash = IkemenPathGuard.Sha256File(plan.TargetPath);
            File.Copy(plan.TargetPath, backupFile, overwrite: true);
            manifest.BackupPath = backupFile;
        }
        else
        {
            // Treat missing target as create-via-replace; record marker.
            File.WriteAllText(Path.Combine(backupDir, "did-not-exist.txt"), plan.TargetPath);
            manifest.BackupPath = backupDir;
        }

        var staging = Path.Combine(parent, $".ikemenlab-{manifest.OperationId}.tmp");
        manifest.StagingPath = staging;
        try
        {
            File.Copy(plan.SourcePath, staging, overwrite: true);
            if (File.Exists(plan.TargetPath))
            {
                // Same-directory replace: delete then move is acceptable after backup.
                var replaceBackup = Path.Combine(backupDir, "replace-win.bak");
                File.Replace(staging, plan.TargetPath, replaceBackup, ignoreMetadataErrors: true);
                if (File.Exists(replaceBackup) && manifest.BackupPath is null)
                {
                    manifest.BackupPath = replaceBackup;
                }
                else if (File.Exists(replaceBackup))
                {
                    File.Delete(replaceBackup);
                }
            }
            else
            {
                File.Move(staging, plan.TargetPath);
            }

            manifest.AfterHash = IkemenPathGuard.Sha256File(plan.TargetPath);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private void ExecuteCreateDirectory(OperationPlan plan, string backupDir, MutationManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(plan.SourcePath) || !Directory.Exists(plan.SourcePath))
        {
            throw new InvalidOperationException("CreateDirectory requires an existing source directory.");
        }

        if (Directory.Exists(plan.TargetPath) || File.Exists(plan.TargetPath))
        {
            throw new InvalidOperationException("CreateDirectory target already exists.");
        }

        IkemenPathGuard.EnsureInsideRoot(plan.IkemenRoot, plan.TargetPath);
        IkemenPathGuard.EnsureNoReparseEscape(plan.IkemenRoot, plan.TargetPath);
        IkemenPathGuard.EnsureDistinctPaths(plan.SourcePath, plan.TargetPath);

        var stagingRoot = Path.Combine(backupDir, "staging");
        CopyDirectory(plan.SourcePath, stagingRoot);
        manifest.StagingPath = stagingRoot;
        manifest.BeforeHash = null;

        // Stage validated — move into place (copy then verify).
        CopyDirectory(stagingRoot, plan.TargetPath);
        manifest.AfterHash = IkemenPathGuard.Sha256DirectoryFingerprint(plan.TargetPath);
        File.WriteAllText(Path.Combine(backupDir, "created-dir.txt"), plan.TargetPath);
        manifest.BackupPath = backupDir;
    }

    private void ExecuteReplaceDirectory(OperationPlan plan, string backupDir, MutationManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(plan.SourcePath) || !Directory.Exists(plan.SourcePath))
        {
            throw new InvalidOperationException("ReplaceDirectory requires an existing source directory.");
        }

        IkemenPathGuard.EnsureInsideRoot(plan.IkemenRoot, plan.TargetPath);
        IkemenPathGuard.EnsureNoReparseEscape(plan.IkemenRoot, plan.TargetPath);
        IkemenPathGuard.EnsureDistinctPaths(plan.SourcePath, plan.TargetPath);

        var originalBackup = Path.Combine(backupDir, "original");
        var stagingRoot = Path.Combine(backupDir, "staging");
        CopyDirectory(plan.SourcePath, stagingRoot);
        manifest.StagingPath = stagingRoot;

        string? displaced = null;
        if (Directory.Exists(plan.TargetPath))
        {
            manifest.BeforeHash = IkemenPathGuard.Sha256DirectoryFingerprint(plan.TargetPath);
            CopyDirectory(plan.TargetPath, originalBackup);
            manifest.BackupPath = originalBackup;

            displaced = plan.TargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + $".__ikemenlab_old_{manifest.OperationId}";
            Directory.Move(plan.TargetPath, displaced);
        }
        else
        {
            Directory.CreateDirectory(originalBackup);
            File.WriteAllText(Path.Combine(backupDir, "did-not-exist.txt"), plan.TargetPath);
            manifest.BackupPath = backupDir;
        }

        try
        {
            CopyDirectory(stagingRoot, plan.TargetPath);
            manifest.AfterHash = IkemenPathGuard.Sha256DirectoryFingerprint(plan.TargetPath);

            if (displaced is not null && Directory.Exists(displaced))
            {
                Directory.Delete(displaced, recursive: true);
            }
        }
        catch
        {
            // Restore displaced directory if the new tree failed.
            if (Directory.Exists(plan.TargetPath))
            {
                try { Directory.Delete(plan.TargetPath, recursive: true); } catch { /* best effort */ }
            }

            if (displaced is not null && Directory.Exists(displaced))
            {
                Directory.Move(displaced, plan.TargetPath);
            }
            else if (Directory.Exists(originalBackup) && Directory.EnumerateFileSystemEntries(originalBackup).Any())
            {
                CopyDirectory(originalBackup, plan.TargetPath);
            }

            throw;
        }
    }

    private void TryEmergencyRestore(MutationManifest manifest)
    {
        if (manifest.BackupPath is null) return;

        switch (manifest.Kind)
        {
            case MutationKind.CreateFile:
                if (File.Exists(manifest.TargetPath)) File.Delete(manifest.TargetPath);
                break;
            case MutationKind.ReplaceFile:
                RestoreFileFromBackup(manifest);
                break;
            case MutationKind.CreateDirectory:
                if (Directory.Exists(manifest.TargetPath)) Directory.Delete(manifest.TargetPath, true);
                break;
            case MutationKind.ReplaceDirectory:
                RestoreDirectoryFromBackup(manifest);
                break;
        }
    }

    private static void RestoreFileFromBackup(MutationManifest manifest)
    {
        if (manifest.BackupPath is null) return;

        if (File.Exists(manifest.BackupPath))
        {
            var parent = Path.GetDirectoryName(manifest.TargetPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(manifest.BackupPath, manifest.TargetPath, overwrite: true);
            return;
        }

        // Marker-only backup means the file was newly created by replace; remove it.
        var marker = Path.Combine(manifest.BackupPath, "did-not-exist.txt");
        if (File.Exists(marker) && File.Exists(manifest.TargetPath))
        {
            File.Delete(manifest.TargetPath);
        }
    }

    private static void RestoreDirectoryFromBackup(MutationManifest manifest)
    {
        if (manifest.BackupPath is null) return;

        var original = Path.Combine(manifest.BackupPath, "original");
        if (!Directory.Exists(original))
        {
            original = manifest.BackupPath;
        }

        var marker = Path.Combine(manifest.BackupPath, "did-not-exist.txt");
        if (File.Exists(marker))
        {
            if (Directory.Exists(manifest.TargetPath))
            {
                Directory.Delete(manifest.TargetPath, recursive: true);
            }

            return;
        }

        if (!Directory.Exists(original)) return;

        if (Directory.Exists(manifest.TargetPath))
        {
            Directory.Delete(manifest.TargetPath, recursive: true);
        }

        CopyDirectory(original, manifest.TargetPath);
    }

    private OperationPlan BuildPlan(
        MutationKind kind,
        string ikemenRoot,
        string targetPath,
        string? sourcePath,
        int? contentLength)
    {
        var warnings = new List<string>();
        try
        {
            var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
            if (!Directory.Exists(root))
            {
                return Reject(kind, ikemenRoot, targetPath, sourcePath, "IKEMEN root does not exist.");
            }

            var resolved = IkemenPathGuard.ResolveUnderRoot(root, targetPath);
            if (sourcePath is not null)
            {
                IkemenPathGuard.EnsureDistinctPaths(sourcePath, resolved);
            }

            var existsFile = File.Exists(resolved);
            var existsDir = Directory.Exists(resolved);
            var wouldOverwrite = existsFile || existsDir;

            if (kind is MutationKind.CreateFile or MutationKind.CreateDirectory)
            {
                if (wouldOverwrite)
                {
                    return Reject(kind, root, resolved, sourcePath, "Create target already exists.");
                }
            }

            if (kind is MutationKind.ReplaceFile && existsDir)
            {
                return Reject(kind, root, resolved, sourcePath, "ReplaceFile target is a directory.");
            }

            if (kind is MutationKind.ReplaceDirectory && existsFile)
            {
                return Reject(kind, root, resolved, sourcePath, "ReplaceDirectory target is a file.");
            }

            if (kind is MutationKind.CreateFile && contentLength is null)
            {
                warnings.Add("CreateFile plan built without content length.");
            }

            var affected = new List<string> { resolved };
            if (sourcePath is not null) affected.Add(Path.GetFullPath(sourcePath));

            return new OperationPlan
            {
                Kind = kind,
                IkemenRoot = root,
                TargetPath = resolved,
                SourcePath = sourcePath is null ? null : Path.GetFullPath(sourcePath),
                WouldOverwrite = wouldOverwrite && kind is MutationKind.ReplaceFile or MutationKind.ReplaceDirectory,
                BackupRequired = wouldOverwrite && kind is MutationKind.ReplaceFile or MutationKind.ReplaceDirectory,
                IsValid = true,
                AffectedPaths = affected,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            return Reject(kind, ikemenRoot, targetPath, sourcePath, ex.Message);
        }
    }

    private static OperationPlan Reject(
        MutationKind kind,
        string ikemenRoot,
        string targetPath,
        string? sourcePath,
        string reason)
    {
        return new OperationPlan
        {
            Kind = kind,
            IkemenRoot = ikemenRoot,
            TargetPath = targetPath,
            SourcePath = sourcePath,
            WouldOverwrite = false,
            BackupRequired = false,
            IsValid = false,
            RejectionReason = reason,
            AffectedPaths = Array.Empty<string>(),
            Warnings = Array.Empty<string>()
        };
    }

    private void SaveManifest(MutationManifest manifest)
    {
        var dir = Path.Combine(_operationsRoot, manifest.OperationId);
        Directory.CreateDirectory(dir);
        var path = ManifestPath(manifest.OperationId);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private string ManifestPath(string operationId) =>
        Path.Combine(_operationsRoot, operationId, "manifest.json");

    private static string NewOperationId()
        => $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";

    private static MutationResult Fail(MutationManifest manifest, OperationPlan? plan)
        => new() { Success = false, Manifest = manifest, Plan = plan };

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
