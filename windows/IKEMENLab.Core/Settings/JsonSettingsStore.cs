using System.Text.Json;
using System.Text.Json.Serialization;

namespace IKEMENLab.Core.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? AppDataPaths.GetSettingsPath();
    }

    public string Path => _path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            if (settings.SchemaVersion <= 0)
            {
                settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, Options);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }
}
