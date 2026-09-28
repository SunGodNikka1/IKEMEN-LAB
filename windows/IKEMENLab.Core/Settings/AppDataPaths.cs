namespace IKEMENLab.Core.Settings;

public static class AppDataPaths
{
    public const string AppFolderName = "IKEMEN Lab";

    public static string GetAppDataDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, AppFolderName);
    }

    public static string GetSettingsPath() => Path.Combine(GetAppDataDirectory(), "settings.json");

    public static string GetBackupsDirectory() => Path.Combine(GetAppDataDirectory(), "backups");

    public static string GetMutationOperationsDirectory() =>
        Path.Combine(GetAppDataDirectory(), "mutations");

    public static string GetInstallStagingDirectory() =>
        Path.Combine(GetAppDataDirectory(), "install-staging");

    public static void EnsureAppDataDirectory()
    {
        Directory.CreateDirectory(GetAppDataDirectory());
    }

    public static void EnsureBackupInfrastructure()
    {
        EnsureAppDataDirectory();
        Directory.CreateDirectory(GetBackupsDirectory());
        Directory.CreateDirectory(GetMutationOperationsDirectory());
        Directory.CreateDirectory(GetInstallStagingDirectory());
    }
}
