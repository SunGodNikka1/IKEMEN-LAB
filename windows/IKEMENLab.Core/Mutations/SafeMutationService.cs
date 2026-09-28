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

    public MutationResult ReplaceFile(string ikemenRoot, string targetPath, string sourceFilePath, bool dryRun = false,
        string? expectedCurrentHash = null)
        => Execute(PlanReplaceFile(ikemenRoot, targetPath, sourceFilePath), createFileContent: null, dryRun, expectedCurrentHash);

    public MutationResult CreateDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory, bool dryRun = false)
        => Execute(PlanCreateDirectory(ikemenRoot, targetDirectory, sourceDirectory), createFileContent: null, dryRun);

    public MutationResult ReplaceDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory, bool dryRun = false)
        => Execute(PlanReplaceDirectory(ikemenRoot, targetDirectory, sourceDirectory), createFileContent: null, dryRun);

    public MutationResult Execute(OperationPlan plan, byte[]? createFileContent = null, bool dryRun = false)
        => Execute(plan, createFileContent, dryRun, expectedCurrentHash: null);

    private MutationResult Execute(OperationPlan plan, byte[]? createFileContent, bool dryRun, string? expectedCurrentHash)
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
                    ExecuteReplaceFile(plan, backupDir, manifest, expectedCurrentHash);
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
            var isDirectory = plan.Kind is MutationKind.CreateDirectory or MutationKind.ReplaceDirectory;
            var failure = FileReplacer.Classify(ex, plan.TargetPath, isDirectory);
            manifest.Status = MutationStatus.Failed;
            manifest.FailureKind = failure.Kind;
            manifest.Win32Error = failure.Win32Error;
            manifest.ErrorDetail = failure.TechnicalDetail ?? ex.Message;
            manifest.Error = failure.Message;
            try
            {
                var restored = TryEmergencyRestore(manifest);
                if (restored)
                {
                    manifest.Error = failure.Message
                        .Replace(FileReplacer.FileUnchanged, "The original file was restored from backup.", StringComparison.Ordinal)
                        .Replace(FileReplacer.FolderUnchanged, "The original folder was restored from backup.", StringComparison.Ordinal);
                }
            }
            catch (Exception restoreEx)
            {
                manifest.Error = failure.Message + " Automatic restore also failed; the backup is at " +
                                 (manifest.BackupPath ?? "(none)") + ".";
                manifest.ErrorDetail += " | restore: " + restoreEx.Message;
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
                    RestoreFileFromBackup(manifest, force: true);
                    break;

                case MutationKind.CreateDirectory:
                    RemoveCreatedDirectory(manifest.TargetPath);
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
            var failure = FileReplacer.Classify(ex, manifest.TargetPath,
                manifest.Kind is MutationKind.CreateDirectory or MutationKind.ReplaceDirectory);
            manifest.Status = MutationStatus.Failed;
            manifest.Error = "Rollback failed: " + failure.Message;
            manifest.ErrorDetail = failure.TechnicalDetail;
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

    private void ExecuteReplaceFile(OperationPlan plan, string backupDir, MutationManifest manifest, string? expectedCurrentHash)
    {
        if (string.IsNullOrWhiteSpace(plan.SourcePath) || !File.Exists(plan.SourcePath))
        {
            throw new InvalidOperationException("ReplaceFile requires an existing source file.");
        }

        IkemenPathGuard.EnsureInsideRoot(plan.IkemenRoot, plan.TargetPath);
        IkemenPathGuard.EnsureNoReparseEscape(plan.IkemenRoot, plan.TargetPath);
        IkemenPathGuard.EnsureDistinctPaths(plan.SourcePath, plan.TargetPath);

        var target = plan.TargetPath;
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("Target has no parent directory.");
        Directory.CreateDirectory(parent);
        FileReplacer.SweepStaleStaging(parent, StaleStagingAge);

        var expectedAfter = FileReplacer.Sha256(plan.SourcePath);
        manifest.ExpectedAfterHash = expectedAfter;

        var targetExists = File.Exists(target);
        var originalAttributes = FileAttributes.Normal;
        var originalCreationUtc = default(DateTime);
        if (targetExists)
        {
            originalAttributes = File.GetAttributes(target);
            originalCreationUtc = File.GetCreationTimeUtc(target);
            manifest.OriginalAttributes = originalAttributes.ToString();
            if ((originalAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SafeMutationException(MutationFailureKind.UnsafeTarget,
                    $"'{Path.GetFileName(target)}' is a link (reparse point); IKEMEN Lab will not replace it. The file was not changed.");
            }

            manifest.BeforeHash = FileReplacer.Sha256(target);
            if (expectedCurrentHash is not null &&
                !string.Equals(expectedCurrentHash, manifest.BeforeHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new SafeMutationException(MutationFailureKind.Conflict,
                    $"'{Path.GetFileName(target)}' was changed by another program after IKEMEN Lab read it. " +
                    "Refresh and try again. The file was not changed.");
            }

            // App-owned backup. It may live on another volume: it is never used as a ReplaceFile backup name.
            var backupFile = Path.Combine(backupDir, "original" + Path.GetExtension(target));
            if (string.IsNullOrEmpty(Path.GetExtension(target)))
            {
                backupFile = Path.Combine(backupDir, "original.bin");
            }

            FileReplacer.CopyDurable(target, backupFile);
            if (!string.Equals(FileReplacer.Sha256(backupFile), manifest.BeforeHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new SafeMutationException(MutationFailureKind.VerificationFailed,
                    "Could not create a verified backup, so the file was not changed.");
            }

            manifest.BackupPath = backupFile;
        }
        else
        {
            // Treat missing target as create-via-replace; record marker.
            File.WriteAllText(Path.Combine(backupDir, "did-not-exist.txt"), target);
            manifest.BackupPath = backupDir;
        }

        // Staging lives beside the target (same volume), so the final swap is a rename.
        var staging = Path.Combine(parent, $".ikemenlab-{manifest.OperationId}.tmp");
        manifest.StagingPath = staging;
        try
        {
            FileReplacer.CopyDurable(plan.SourcePath, staging);
            if (!string.Equals(FileReplacer.Sha256(staging), expectedAfter, StringComparison.OrdinalIgnoreCase))
            {
                throw new SafeMutationException(MutationFailureKind.VerificationFailed,
                    "The staged replacement did not match its source, so the file was not changed.");
            }

            if (targetExists)
            {
                manifest.ReplaceMethod = SwapPreservingMetadata(staging, target, originalAttributes, originalCreationUtc, manifest);
            }
            else
            {
                File.Move(staging, target);
                manifest.ReplaceMethod = "Move";
            }

            manifest.AfterHash = FileReplacer.Sha256(target);
            if (!string.Equals(manifest.AfterHash, expectedAfter, StringComparison.OrdinalIgnoreCase))
            {
                throw new SafeMutationException(MutationFailureKind.VerificationFailed,
                    $"'{Path.GetFileName(target)}' did not match the intended content after writing. The file was not changed.");
            }
        }
        finally
        {
            TryDeleteFile(staging);
        }
    }

    /// <summary>
    /// Swaps <paramref name="staging"/> into <paramref name="target"/>. A ReadOnly attribute is lifted
    /// only for the swap and re-applied afterwards; the MoveFileEx fallback restores the original
    /// attributes and creation time (ReplaceFile preserves them itself).
    /// </summary>
    private static string SwapPreservingMetadata(
        string staging, string target, FileAttributes originalAttributes, DateTime originalCreationUtc, MutationManifest? manifest)
    {
        var readOnly = (originalAttributes & FileAttributes.ReadOnly) != 0;
        if (readOnly)
        {
            File.SetAttributes(target, originalAttributes & ~FileAttributes.ReadOnly);
            if (manifest is not null) manifest.ReadOnlyPreserved = true;
        }

        try
        {
            var method = FileReplacer.Replace(staging, target);
            if (method == "MoveFileEx")
            {
                TryRestoreMetadata(target, originalAttributes & ~FileAttributes.ReadOnly, originalCreationUtc);
            }

            return method;
        }
        finally
        {
            if (readOnly && File.Exists(target))
            {
                File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
            }
        }
    }

    private static void TryRestoreMetadata(string target, FileAttributes attributes, DateTime creationUtc)
    {
        try
        {
            var keep = attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive |
                                     FileAttributes.NotContentIndexed);
            File.SetAttributes(target, keep == 0 ? FileAttributes.Normal : keep);
            if (creationUtc != default) File.SetCreationTimeUtc(target, creationUtc);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(1);

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

        var target = Path.TrimEndingDirectorySeparator(plan.TargetPath);
        var staging = StageDirectoryBeside(plan.SourcePath, target, manifest);
        manifest.BeforeHash = null;
        try
        {
            File.WriteAllText(Path.Combine(backupDir, "created-dir.txt"), target);

            // A same-parent rename: the folder appears complete or not at all, and fails (rather than
            // merging) if something created the target meanwhile.
            FileReplacer.MoveDirectory(staging, target, target);

            // Only now does the target belong to this operation, so only now may a failure remove it.
            manifest.BackupPath = backupDir;
            VerifyDirectory(target, manifest);
        }
        finally
        {
            FileReplacer.TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to a hidden staging folder next to <paramref name="target"/>
    /// (same volume, so the final swap is a rename) and verifies the copy against the source.
    /// </summary>
    private static string StageDirectoryBeside(string source, string target, MutationManifest manifest)
    {
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        FileReplacer.SweepStaleStaging(parent, StaleStagingAge);

        var staging = Path.Combine(parent, $".ikemenlab-{manifest.OperationId}.tmp");
        manifest.StagingPath = staging;
        manifest.ExpectedAfterHash = IkemenPathGuard.Sha256DirectoryFingerprint(source);
        try
        {
            CopyDirectory(source, staging);
            if (!string.Equals(IkemenPathGuard.Sha256DirectoryFingerprint(staging), manifest.ExpectedAfterHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SafeMutationException(MutationFailureKind.VerificationFailed,
                    $"The staged copy of '{Path.GetFileName(target)}' did not match the source (the source may have " +
                    $"changed while it was being copied). {FileReplacer.FolderUnchanged}");
            }
        }
        catch
        {
            FileReplacer.TryDeleteDirectory(staging);
            throw;
        }

        return staging;
    }

    private static void VerifyDirectory(string target, MutationManifest manifest)
    {
        manifest.AfterHash = IkemenPathGuard.Sha256DirectoryFingerprint(target);
        if (!string.Equals(manifest.AfterHash, manifest.ExpectedAfterHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new SafeMutationException(MutationFailureKind.VerificationFailed,
                $"'{Path.GetFileName(target)}' did not match the installed content after the swap.",
                $"expected={manifest.ExpectedAfterHash} actual={manifest.AfterHash}");
        }
    }

    private static void RemoveCreatedDirectory(string target)
    {
        if (!FileReplacer.TryDeleteDirectory(target))
        {
            throw new SafeMutationException(MutationFailureKind.InUse,
                $"'{Path.GetFileName(Path.TrimEndingDirectorySeparator(target))}' could not be removed because a file " +
                "inside it is in use. Close programs using it and try again.");
        }
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

        var target = Path.TrimEndingDirectorySeparator(plan.TargetPath);
        var originalBackup = Path.Combine(backupDir, "original");
        var staging = StageDirectoryBeside(plan.SourcePath, target, manifest);
        var displaced = Path.Combine(Path.GetDirectoryName(target)!, $".ikemenlab-{manifest.OperationId}.old");
        var committed = false;
        try
        {
            if (!Directory.Exists(target))
            {
                Directory.CreateDirectory(originalBackup);
                File.WriteAllText(Path.Combine(backupDir, "did-not-exist.txt"), target);
                FileReplacer.MoveDirectory(staging, target, target);
                manifest.BackupPath = backupDir;
                committed = true;
                VerifyDirectory(target, manifest);
                return;
            }

            // Verified app-owned backup first (Undo needs it); only then is the live folder touched.
            manifest.BeforeHash = IkemenPathGuard.Sha256DirectoryFingerprint(target);
            CopyDirectory(target, originalBackup);
            if (!string.Equals(IkemenPathGuard.Sha256DirectoryFingerprint(originalBackup), manifest.BeforeHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SafeMutationException(MutationFailureKind.VerificationFailed,
                    $"The backup of '{Path.GetFileName(target)}' could not be verified. {FileReplacer.FolderUnchanged}");
            }

            manifest.BackupPath = originalBackup;

            // Two same-parent renames. If the first fails (a file inside is open) the folder is untouched;
            // if the second fails the original is renamed straight back.
            FileReplacer.MoveDirectory(target, displaced, target);
            try
            {
                FileReplacer.MoveDirectory(staging, target, target);
            }
            catch
            {
                Directory.Move(displaced, target);
                throw;
            }

            try
            {
                VerifyDirectory(target, manifest);
            }
            catch
            {
                // Put the untouched original back; the rejected copy is discarded.
                var rejected = Path.Combine(Path.GetDirectoryName(target)!, $".ikemenlab-{manifest.OperationId}-rejected.tmp");
                Directory.Move(target, rejected);
                Directory.Move(displaced, target);
                FileReplacer.TryDeleteDirectory(rejected);
                throw;
            }

            committed = true;
        }
        finally
        {
            FileReplacer.TryDeleteDirectory(staging);
            if (committed && Directory.Exists(displaced) && !FileReplacer.TryDeleteDirectory(displaced))
            {
                // Cleanup only: the new content is in place and a full backup exists. Leave the hidden
                // displaced folder for the user rather than failing a completed install.
                manifest.ErrorDetail = $"Could not remove the previous version at '{displaced}' (a file inside may be open).";
            }
        }
    }

    /// <summary>Returns true when the target had to be (and was) restored from backup.</summary>
    private static bool TryEmergencyRestore(MutationManifest manifest)
    {
        if (manifest.BackupPath is null) return false;

        switch (manifest.Kind)
        {
            case MutationKind.CreateFile:
                if (File.Exists(manifest.TargetPath)) File.Delete(manifest.TargetPath);
                return false;
            case MutationKind.ReplaceFile:
                return RestoreFileFromBackup(manifest, force: false);
            case MutationKind.CreateDirectory:
                // BackupPath is only set once the target was created by this operation.
                RemoveCreatedDirectory(manifest.TargetPath);
                return false;
            case MutationKind.ReplaceDirectory:
                return RestoreDirectoryFromBackup(manifest);
        }

        return false;
    }

    /// <summary>
    /// Restores a ReplaceFile target from its app-owned backup. A target that still holds its original
    /// bytes is left untouched (the usual case when the swap itself was refused). The restore is staged
    /// beside the target and swapped the same way as a replace, so it works across volumes and keeps a
    /// ReadOnly attribute.
    /// </summary>
    private static bool RestoreFileFromBackup(MutationManifest manifest, bool force)
    {
        if (manifest.BackupPath is null) return false;
        var target = manifest.TargetPath;

        if (File.Exists(manifest.BackupPath))
        {
            if (File.Exists(target) && manifest.BeforeHash is not null &&
                string.Equals(FileReplacer.Sha256(target), manifest.BeforeHash, StringComparison.OrdinalIgnoreCase))
            {
                manifest.OriginalVerifiedIntact = true;
                return false;
            }

            if (!force && manifest.BeforeHash is null) return false;

            var parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, $".ikemenlab-{manifest.OperationId}-restore.tmp");
            var attributes = Enum.TryParse<FileAttributes>(manifest.OriginalAttributes, out var parsed)
                ? parsed
                : FileAttributes.Normal;
            try
            {
                FileReplacer.CopyDurable(manifest.BackupPath, staging);
                if (File.Exists(target))
                {
                    var current = File.GetAttributes(target);
                    SwapPreservingMetadata(staging, target,
                        (current & ~FileAttributes.ReadOnly) | (attributes & FileAttributes.ReadOnly), default, manifest: null);
                }
                else
                {
                    File.Move(staging, target);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
                    }
                }
            }
            finally
            {
                TryDeleteFile(staging);
            }

            if (manifest.BeforeHash is not null &&
                !string.Equals(FileReplacer.Sha256(target), manifest.BeforeHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new SafeMutationException(MutationFailureKind.VerificationFailed,
                    "The restored file did not match the backup.");
            }

            manifest.OriginalVerifiedIntact = true;
            return true;
        }

        // Marker-only backup means the file was newly created by replace; remove it.
        var marker = Path.Combine(manifest.BackupPath, "did-not-exist.txt");
        if (File.Exists(marker) && File.Exists(target))
        {
            File.SetAttributes(target, FileAttributes.Normal);
            File.Delete(target);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Restores a ReplaceDirectory target from its app-owned backup. A target that still matches the
    /// original fingerprint is left untouched (the usual case when the swap itself was refused). The
    /// backup is staged beside the target and renamed into place, so the live folder is never deleted
    /// before a complete, verified replacement exists.
    /// </summary>
    private static bool RestoreDirectoryFromBackup(MutationManifest manifest)
    {
        if (manifest.BackupPath is null) return false;
        var target = Path.TrimEndingDirectorySeparator(manifest.TargetPath);

        var marker = Path.Combine(manifest.BackupPath, "did-not-exist.txt");
        if (File.Exists(marker))
        {
            if (!Directory.Exists(target)) return false;
            RemoveCreatedDirectory(target);
            return true;
        }

        var original = Path.Combine(manifest.BackupPath, "original");
        if (!Directory.Exists(original))
        {
            original = manifest.BackupPath;
        }

        if (!Directory.Exists(original)) return false;

        if (Directory.Exists(target) && manifest.BeforeHash is not null &&
            string.Equals(IkemenPathGuard.Sha256DirectoryFingerprint(target), manifest.BeforeHash, StringComparison.OrdinalIgnoreCase))
        {
            manifest.OriginalVerifiedIntact = true;
            return false;
        }

        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".ikemenlab-{manifest.OperationId}-restore.tmp");
        var displaced = Path.Combine(parent, $".ikemenlab-{manifest.OperationId}-restore.old");
        try
        {
            FileReplacer.TryDeleteDirectory(staging);
            CopyDirectory(original, staging);
            if (manifest.BeforeHash is not null &&
                !string.Equals(IkemenPathGuard.Sha256DirectoryFingerprint(staging), manifest.BeforeHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new SafeMutationException(MutationFailureKind.VerificationFailed,
                    "The backup copy did not match the original folder; nothing was restored.");
            }

            if (Directory.Exists(target))
            {
                FileReplacer.MoveDirectory(target, displaced, target);
            }

            try
            {
                FileReplacer.MoveDirectory(staging, target, target);
            }
            catch
            {
                if (Directory.Exists(displaced) && !Directory.Exists(target)) Directory.Move(displaced, target);
                throw;
            }
        }
        finally
        {
            FileReplacer.TryDeleteDirectory(staging);
        }

        FileReplacer.TryDeleteDirectory(displaced);
        manifest.OriginalVerifiedIntact = true;
        return true;
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
