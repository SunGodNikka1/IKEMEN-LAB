using System.Text.Json;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Collections;

/// <summary>
/// Remembers which app-owned collection was last successfully activated for an install.
/// Lives under %LOCALAPPDATA%/IKEMEN Lab/ — never inside the IKEMEN root.
/// Filesystem select.def remains authoritative; this is a hint for UI status.
/// </summary>
public sealed class ActiveCollectionStore
{
    private sealed record Document(int SchemaVersion, IReadOnlyList<ActiveCollectionRecord> Records);

    public sealed record ActiveCollectionRecord(
        string LibraryRoot,
        Guid? CollectionId,
        bool IsAllCharacters,
        DateTime ActivatedAtUtc);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string FilePath { get; }

    public ActiveCollectionStore(string? path = null)
        => FilePath = Path.GetFullPath(path ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "active-collection.json"));

    public ActiveCollectionRecord? Get(string libraryRoot)
    {
        var root = CollectionStore.NormalizeRoot(libraryRoot);
        return Read().FirstOrDefault(r => SameRoot(r.LibraryRoot, root));
    }

    public void Set(string libraryRoot, Guid? collectionId, bool isAllCharacters)
    {
        var root = CollectionStore.NormalizeRoot(libraryRoot);
        EnsureOutsideIkemen(root);
        var all = Read().Where(r => !SameRoot(r.LibraryRoot, root)).ToList();
        all.Add(new ActiveCollectionRecord(root, isAllCharacters ? null : collectionId, isAllCharacters, DateTime.UtcNow));
        Write(all);
    }

    public void Clear(string libraryRoot)
    {
        var root = CollectionStore.NormalizeRoot(libraryRoot);
        var all = Read().Where(r => !SameRoot(r.LibraryRoot, root)).ToList();
        Write(all);
    }

    private IReadOnlyList<ActiveCollectionRecord> Read()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), Options);
            return doc?.Records ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void Write(IReadOnlyList<ActiveCollectionRecord> records)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new Document(1, records.ToArray()), Options);
        File.WriteAllText(FilePath, json);
    }

    private void EnsureOutsideIkemen(string libraryRoot)
    {
        var full = Path.GetFullPath(FilePath);
        var root = Path.GetFullPath(libraryRoot);
        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            full.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Active collection metadata must not be stored inside the IKEMEN install.");
        }
    }

    private static bool SameRoot(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
