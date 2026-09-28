using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Library;

/// <summary>How a Date Added value was established.</summary>
public enum DateAddedSource
{
    /// <summary>Recorded by IKEMEN Lab when its own install succeeded (exact).</summary>
    Installed,

    /// <summary>First detected by IKEMEN Lab after tracking began (bounded by consecutive scans).</summary>
    FirstSeen,

    /// <summary>Existed before tracking began; estimated once from file-system evidence.</summary>
    LegacyEstimate
}

public sealed class DateAddedRecord
{
    public DateTime DateAddedUtc { get; set; }
    public DateAddedSource Source { get; set; }

    /// <summary>When IKEMEN Lab wrote this record.</summary>
    public DateTime RecordedUtc { get; set; }

    /// <summary>Set while the item's folder/DEF is absent from the installation.</summary>
    public DateTime? MissingSinceUtc { get; set; }

    /// <summary>Last root-relative DEF path seen for this identity (diagnostics only).</summary>
    public string? LastKnownPath { get; set; }
}

public sealed class DateAddedDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Root { get; set; } = string.Empty;

    /// <summary>First scan of this installation: everything present then is a legacy estimate.</summary>
    public DateTime? BaselineUtc { get; set; }

    public DateTime? LastScanUtc { get; set; }
    public Dictionary<string, DateAddedRecord> Items { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// App-owned Date Added records, one JSON file per IKEMEN installation under
/// %LOCALAPPDATA%\IKEMEN Lab\library\date-added. Never stored inside the IKEMEN root.
/// </summary>
public sealed class DateAddedStore
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public DateAddedStore(string directory)
    {
        Directory = System.IO.Path.GetFullPath(directory);
    }

    public string Directory { get; }

    public static string DefaultDirectory =>
        System.IO.Path.Combine(AppDataPaths.GetAppDataDirectory(), "library", "date-added");

    public static DateAddedStore CreateDefault() => new(DefaultDirectory);

    public static string NormalizeRoot(string root)
        => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root.Trim()));

    public string PathFor(string root)
    {
        var key = NormalizeRoot(root).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return System.IO.Path.Combine(Directory, hash + ".json");
    }

    public DateAddedDocument Read(string root)
    {
        var path = PathFor(root);
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            return Load(path, root);
        }
    }

    /// <summary>Loads, applies <paramref name="mutate"/> and saves atomically when it reports a change.</summary>
    public T Update<T>(string root, Func<DateAddedDocument, (T Result, bool Changed)> mutate)
    {
        var path = PathFor(root);
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            var doc = Load(path, root);
            var (result, changed) = mutate(doc);
            if (changed) Save(path, doc);
            return result;
        }
    }

    private static DateAddedDocument Load(string path, string root)
    {
        var normalized = NormalizeRoot(root);
        if (!File.Exists(path)) return new DateAddedDocument { Root = normalized };

        try
        {
            var doc = JsonSerializer.Deserialize<DateAddedDocument>(File.ReadAllText(path), Json);
            if (doc is not null &&
                string.Equals(NormalizeRoot(doc.Root), normalized, StringComparison.OrdinalIgnoreCase))
            {
                doc.Items = new Dictionary<string, DateAddedRecord>(doc.Items ?? [], StringComparer.Ordinal);
                return doc;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
            throw; // never silently start over when the file exists but cannot be read
        }

        // Unreadable or foreign content: keep it for inspection, then start a new document.
        try { File.Copy(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss"), overwrite: false); }
        catch (IOException) { }
        return new DateAddedDocument { Root = normalized };
    }

    private void Save(string path, DateAddedDocument doc)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
