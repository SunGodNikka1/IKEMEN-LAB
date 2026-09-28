namespace IKEMENLab.Core.Mutations;

public interface ISafeMutationService
{
    OperationPlan PlanCreateFile(string ikemenRoot, string targetPath, byte[] content);
    OperationPlan PlanReplaceFile(string ikemenRoot, string targetPath, string sourceFilePath);
    OperationPlan PlanCreateDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory);
    OperationPlan PlanReplaceDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory);

    MutationResult CreateFile(string ikemenRoot, string targetPath, byte[] content, bool dryRun = false);
    MutationResult ReplaceFile(string ikemenRoot, string targetPath, string sourceFilePath, bool dryRun = false);
    MutationResult CreateDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory, bool dryRun = false);
    MutationResult ReplaceDirectory(string ikemenRoot, string targetDirectory, string sourceDirectory, bool dryRun = false);

    MutationResult Execute(OperationPlan plan, byte[]? createFileContent = null, bool dryRun = false);
    MutationResult Rollback(string operationId);
    MutationManifest? LoadManifest(string operationId);
}
