namespace IKEMENLab.Core.Mutations;

public enum MutationKind
{
    CreateFile,
    ReplaceFile,
    CreateDirectory,
    ReplaceDirectory,
    DeleteDirectory
}

public enum MutationStatus
{
    Planned,
    DryRun,
    Succeeded,
    Failed,
    RolledBack
}
