using IKEMENLab.Core.Models;

namespace IKEMENLab.Core.Services;

public sealed class IkemenInstallationValidator
{
    public const string ExeFileName = "Ikemen_GO.exe";

    public InstallationCheck Validate(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new InstallationCheck
            {
                RootPath = string.Empty,
                CharsPresent = false,
                StagesPresent = false,
                DataPresent = false,
                ExePresent = false
            };
        }

        var root = Path.GetFullPath(rootPath.Trim());
        var exePath = Path.Combine(root, ExeFileName);

        return new InstallationCheck
        {
            RootPath = root,
            CharsPresent = Directory.Exists(Path.Combine(root, "chars")),
            StagesPresent = Directory.Exists(Path.Combine(root, "stages")),
            DataPresent = Directory.Exists(Path.Combine(root, "data")),
            ExePresent = File.Exists(exePath),
            ExePath = File.Exists(exePath) ? exePath : null
        };
    }
}
