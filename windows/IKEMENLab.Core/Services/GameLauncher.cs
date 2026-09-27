using System.Diagnostics;

namespace IKEMENLab.Core.Services;

public sealed class GameLauncher : IGameLauncher
{
    private readonly IkemenInstallationValidator _validator = new();

    public bool CanLaunch(string? rootPath)
    {
        return _validator.Validate(rootPath).CanLaunch;
    }

    public LaunchResult Launch(string rootPath)
    {
        var check = _validator.Validate(rootPath);
        if (!check.CanLaunch || check.ExePath is null)
        {
            return new LaunchResult
            {
                Success = false,
                Error = "Ikemen_GO.exe not found or installation incomplete."
            };
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = check.ExePath,
                WorkingDirectory = check.RootPath,
                UseShellExecute = false
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return new LaunchResult
                {
                    Success = false,
                    Error = "Process.Start returned null."
                };
            }

            return new LaunchResult
            {
                Success = true,
                ProcessId = process.Id
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }
}
