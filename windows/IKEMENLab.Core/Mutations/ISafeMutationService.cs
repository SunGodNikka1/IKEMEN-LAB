namespace IKEMENLab.Core.Mutations;

public interface ISafeMutationService
{
    OperationPlan PlanCreateFile(string ikemenRoot, string targetPath, byte[] content);
    OperationPlan PlanReplaceFile(string ikemenRoot, string targetPath, string sourceFilePath);
    OperationPlan PlanCreateDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory);
    OperationPlan PlanReplaceDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory);
    OperationPlan PlanDeleteDirectory(string ikemenRoot, string targetDirectory);

    MutationResult CreateFile(string ikemenRoot, string targetPath, byte[] content, bool dryRun = false);
    /// <param name="expectedCurrentHash">
    /// Optional SHA-256 (hex) the target must still have; if another program changed the file since
    /// the caller read it, the operation fails with <see cref="MutationFailureKind.Conflict"/>.
    /// </param>
    MutationResult ReplaceFile(string ikemenRoot, string targetPath, string sourceFilePath, bool dryRun = false,
        string? expectedCurrentHash = null);
    MutationResult CreateDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory, bool dryRun = false);
    MutationResult ReplaceDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory, bool dryRun = false);

    /// <summary>
    /// Removes a folder from the installation. A verified copy is kept as the operation's backup first,
    /// so <see cref="Rollback"/> can put the folder back exactly as it was.
    /// </summary>
    MutationResult DeleteDirectory(string ikemenRoot, string targetDirectory, bool dryRun = false);

    MutationResult Execute(OperationPlan plan, byte[]? createFileContent = null, bool dryRun = false);
    MutationResult Rollback(string operationId);
    MutationManifest? LoadManifest(string operationId);
}
