using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Library;

public sealed class PrimaryDefChoice
{
    /// <summary>DEF path relative to the character folder, '/' separators ("Muzan_AI.def").</summary>
    public string Def { get; set; } = string.Empty;

    public DateTime ChosenUtc { get; set; }
}

public sealed class PrimaryDefDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Root { get; set; } = string.Empty;

    /// <summary>Keyed by <see cref="ContentIdentity.ForCharacterFolder"/> ("character:muzan").</summary>
    public Dictionary<string, PrimaryDefChoice> Choices { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// DEFs the user explicitly chose for a character folder, one JSON file per IKEMEN installation under
/// %LOCALAPPDATA%\IKEMEN Lab\library\primary-def. Never stored in the character folder, select.def or
/// anywhere else inside the IKEMEN root. Only explicit choices are saved; everything else is re-derived
/// by <see cref="PrimaryDefResolver"/>.
/// </summary>
public sealed class PrimaryDefStore
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PrimaryDefStore(string directory)
    {
        Directory = System.IO.Path.GetFullPath(directory);
    }

    public string Directory { get; }

    public static string DefaultDirectory =>
        System.IO.Path.Combine(AppDataPaths.GetAppDataDirectory(), "library", "primary-def");

    public static PrimaryDefStore CreateDefault() => new(DefaultDirectory);

    public string PathFor(string root)
    {
        var key = DateAddedStore.NormalizeRoot(root).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return System.IO.Path.Combine(Directory, hash + ".json");
    }

    /// <summary>Saved choices by character identity → DEF relative to the character folder.</summary>
    public IReadOnlyDictionary<string, string> Read(string root)
    {
        var path = PathFor(root);
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            return Load(path, root).Choices.ToDictionary(kv => kv.Key, kv => kv.Value.Def, StringComparer.Ordinal);
        }
    }

    public string? Get(string root, string characterFolder)
        => Read(root).TryGetValue(ContentIdentity.ForCharacterFolder(characterFolder), out var def) ? def : null;

    public void Set(string root, string characterFolder, string relativeDef)
        => Update(root, doc =>
        {
            doc.Choices[ContentIdentity.ForCharacterFolder(characterFolder)] = new PrimaryDefChoice
            {
                Def = relativeDef.Replace('\\', '/').TrimStart('/'),
                ChosenUtc = DateTime.UtcNow
            };
            return true;
        });

    public void Remove(string root, string characterFolder)
        => Update(root, doc => doc.Choices.Remove(ContentIdentity.ForCharacterFolder(characterFolder)));

    private void Update(string root, Func<PrimaryDefDocument, bool> mutate)
    {
        var path = PathFor(root);
        lock (Locks.GetOrAdd(path, _ => new object()))
        {
            var doc = Load(path, root);
            if (!mutate(doc)) return;
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private static PrimaryDefDocument Load(string path, string root)
    {
        var normalized = DateAddedStore.NormalizeRoot(root);
        if (!File.Exists(path)) return new PrimaryDefDocument { Root = normalized };

        try
        {
            var doc = JsonSerializer.Deserialize<PrimaryDefDocument>(File.ReadAllText(path), Json);
            if (doc is not null &&
                string.Equals(DateAddedStore.NormalizeRoot(doc.Root), normalized, StringComparison.OrdinalIgnoreCase))
            {
                doc.Choices = new Dictionary<string, PrimaryDefChoice>(doc.Choices ?? [], StringComparer.Ordinal);
                return doc;
            }
        }
        catch (JsonException)
        {
        }

        // Unreadable or foreign content: keep it for inspection, then start a new document.
        try { File.Copy(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss"), overwrite: false); }
        catch (IOException) { }
        return new PrimaryDefDocument { Root = normalized };
    }
}
