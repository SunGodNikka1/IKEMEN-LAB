namespace IKEMENLab.Core.Services;

public interface IGameLauncher
{
    bool CanLaunch(string? rootPath);
    LaunchResult Launch(string rootPath);
}

public sealed class LaunchResult
{
    public bool Success { get; init; }
    public int? ProcessId { get; init; }
    public string? Error { get; init; }
}
