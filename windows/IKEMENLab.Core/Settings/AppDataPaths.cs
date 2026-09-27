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

    public static void EnsureAppDataDirectory()
    {
        Directory.CreateDirectory(GetAppDataDirectory());
    }
}
