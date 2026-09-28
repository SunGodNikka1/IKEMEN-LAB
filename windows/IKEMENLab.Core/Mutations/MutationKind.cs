namespace IKEMENLab.Core.Mutations;

public enum MutationKind
{
    CreateFile,
    ReplaceFile,
    CreateDirectory,
    ReplaceDirectory
}

public enum MutationStatus
{
    Planned,
    DryRun,
    Succeeded,
    Failed,
    RolledBack
}
