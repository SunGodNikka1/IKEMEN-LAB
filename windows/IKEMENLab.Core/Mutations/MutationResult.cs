namespace IKEMENLab.Core.Mutations;

public sealed class MutationResult
{
    public required bool Success { get; init; }
    public required MutationManifest Manifest { get; init; }
    public OperationPlan? Plan { get; init; }
    public string? Error => Manifest.Error;
    public string OperationId => Manifest.OperationId;
}
