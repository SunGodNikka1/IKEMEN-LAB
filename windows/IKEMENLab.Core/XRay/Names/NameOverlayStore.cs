using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.XRay.Names;

/// <summary>A name the user gave to one semantic object, with the fingerprint of the object it was given to.</summary>
public sealed class NameRecord
{
    public string Label { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;

    /// <summary><see cref="NameFingerprint"/> of the object when the name was given or last confirmed.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>X-Ray's own name for the object at that time ("State 200"), for review screens.</summary>
    public string DefaultName { get; set; } = string.Empty;

    public DateTime RenamedUtc { get; set; }
}

public sealed class NameOverlayDocument
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Normalised full path of the character folder this overlay belongs to.</summary>
    public string CharacterFolder { get; set; } = string.Empty;

    /// <summary>Records keyed by semantic object id (<c>state:200</c>, <c>ability:200</c>, <c>cmd:QCF_x</c> …).</summary>
    public SortedDictionary<string, NameRecord> Names { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// App-owned storage of user names, one JSON file per character folder under
/// <c>%LOCALAPPDATA%\IKEMEN Lab\xray\names\</c>. Character files are never written.
/// </summary>
public sealed class NameOverlayStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public NameOverlayStore(string directory) => Directory = Path.GetFullPath(directory);

    public string Directory { get; }

    public static string DefaultDirectory => Path.Combine(AppDataPaths.GetAppDataDirectory(), "xray", "names");

    public static NameOverlayStore CreateDefault() => new(DefaultDirectory);

    public static string NormalizeFolder(string characterFolder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(characterFolder.Trim()));

    public string PathFor(string characterFolder)
    {
        var key = NormalizeFolder(characterFolder).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return Path.Combine(Directory, hash + ".json");
    }

    public NameOverlayDocument Load(string characterFolder)
    {
        var path = PathFor(characterFolder);
        var folder = NormalizeFolder(characterFolder);
        lock (Gate)
        {
            if (!File.Exists(path)) return new NameOverlayDocument { CharacterFolder = folder };
            try
            {
                var doc = JsonSerializer.Deserialize<NameOverlayDocument>(File.ReadAllText(path), Json);
                if (doc is not null && string.Equals(NormalizeFolder(doc.CharacterFolder), folder, StringComparison.OrdinalIgnoreCase))
                {
                    doc.Names = new SortedDictionary<string, NameRecord>(doc.Names ?? new(), StringComparer.Ordinal);
                    return doc;
                }
            }
            catch (JsonException)
            {
            }

            // Unreadable or foreign: keep it for inspection, start empty (never guess names).
            try { File.Copy(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss"), overwrite: false); }
            catch (IOException) { }
            return new NameOverlayDocument { CharacterFolder = folder };
        }
    }

    public void Save(NameOverlayDocument doc)
    {
        var path = PathFor(doc.CharacterFolder);
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
