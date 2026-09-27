using System.Globalization;
using System.Text.Json;
using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Config;

/// <summary>Values read from the IKEMEN configuration. Null means "not present / unreadable".</summary>
public sealed record IkemenConfig
{
    public string? SourcePath { get; init; }
    public bool Exists => SourcePath is not null;
    public bool? VSync { get; init; }
    public bool? Fullscreen { get; init; }
    public int? MasterVolume { get; init; }
    public string? Motif { get; init; }

    public static IkemenConfig Missing { get; } = new();
}

/// <summary>
/// Read-only access to save/config.ini (IKEMEN GO 1.0) with a save/config.json fallback for
/// older builds. Never writes.
/// </summary>
public static class IkemenConfigReader
{
    public static IkemenConfig Read(string root)
    {
        var ini = Path.Combine(root, "save", "config.ini");
        if (File.Exists(ini))
        {
            var parsed = DefParser.ParseFile(ini);
            if (parsed is not null)
            {
                return new IkemenConfig
                {
                    SourcePath = ini,
                    VSync = ParseBool(parsed.Value("vsync", "video")),
                    Fullscreen = ParseBool(parsed.Value("fullscreen", "video")),
                    MasterVolume = ParseInt(parsed.Value("mastervolume", "sound")),
                    Motif = NullIfEmpty(parsed.Value("motif", "config"))
                };
            }
        }

        var json = Path.Combine(root, "save", "config.json");
        if (File.Exists(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                var r = doc.RootElement;
                return new IkemenConfig
                {
                    SourcePath = json,
                    VSync = JsonBool(r, "VRetrace") ?? JsonBool(r, "VSync"),
                    Fullscreen = JsonBool(r, "Fullscreen"),
                    MasterVolume = JsonInt(r, "MasterVolume"),
                    Motif = r.TryGetProperty("Motif", out var m) && m.ValueKind == JsonValueKind.String
                        ? NullIfEmpty(m.GetString())
                        : null
                };
            }
            catch (JsonException)
            {
                return IkemenConfig.Missing;
            }
            catch (IOException)
            {
                return IkemenConfig.Missing;
            }
        }

        return IkemenConfig.Missing;
    }

    private static bool? ParseBool(string? value)
    {
        if (value is null) return null;
        var v = value.Trim().ToLowerInvariant();
        return v switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n != 0 : null
        };
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool? JsonBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when p.TryGetInt32(out var n) => n != 0,
            _ => null
        };
    }

    private static int? JsonInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)
            ? n
            : null;
}
