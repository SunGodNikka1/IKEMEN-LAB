using System.Text.Json;
using System.Text.Json.Serialization;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Collections;

/// <summary>App-owned metadata only. No activation, roster writer or asset deletion dependencies.</summary>
public sealed class CollectionStore
{
    private sealed record Document(int SchemaVersion, IReadOnlyList<CharacterCollection> Collections);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public string FilePath { get; }
    public CollectionStore(string? path = null)
        => FilePath = Path.GetFullPath(path ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "collections.json"));

    public static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public IReadOnlyList<CharacterCollection> Load(string libraryRoot)
        => Read().Where(c => SameRoot(c.LibraryRoot, libraryRoot)).ToArray();

    public CharacterCollection Create(string root, string name, CollectionKind kind = CollectionKind.Manual,
        IReadOnlyList<CollectionRule>? rules = null, RuleMatch match = RuleMatch.All)
    {
        var all = Read().ToList();
        var item = new CharacterCollection { Id = Guid.NewGuid(), Name = name.Trim(), LibraryRoot = NormalizeRoot(root),
            Kind = kind, Rules = rules?.ToArray() ?? [], Match = match };
        CheckName(all, item);
        all.Add(item);
        Write(all, root);
        return item;
    }

    public CharacterCollection Edit(string root, Guid id, string name, IReadOnlyList<CollectionRule> rules, RuleMatch match)
        => Update(root, id, c => c with { Name = name.Trim(), Rules = rules.ToArray(), Match = match });

    public CharacterCollection Rename(string root, Guid id, string name)
        => Update(root, id, c => c with { Name = name.Trim() });

    public void Delete(string root, Guid id)
    {
        var all = Read().ToList();
        var item = Find(all, root, id);
        all.Remove(item);
        Write(all, root);
    }

    public CharacterCollection Add(string root, Guid id, IEnumerable<CharacterEntry> characters)
        => Update(root, id, c =>
        {
            RequireManual(c);
            var members = c.Members.Concat(characters.Select(e => new CollectionMember(CollectionMembership.Key(e.Id), e.DisplayName)))
                .DistinctBy(m => CollectionMembership.Key(m.CharacterId), StringComparer.OrdinalIgnoreCase).ToArray();
            return c with { Members = members };
        });

    public CharacterCollection Remove(string root, Guid id, IEnumerable<string> characterIds)
        => Update(root, id, c =>
        {
            RequireManual(c);
            var ids = characterIds.Select(CollectionMembership.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return c with { Members = c.Members.Where(m => !ids.Contains(CollectionMembership.Key(m.CharacterId))).ToArray() };
        });

    private CharacterCollection Update(string root, Guid id, Func<CharacterCollection, CharacterCollection> update)
    {
        var all = Read().ToList();
        var old = Find(all, root, id);
        var item = update(old) with { ModifiedAtUtc = DateTime.UtcNow };
        CheckName(all, item);
        all[all.IndexOf(old)] = item;
        Write(all, root);
        return item;
    }

    private IReadOnlyList<CharacterCollection> Read()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), Options);
            if (doc is null || doc.SchemaVersion != 1 || doc.Collections is null)
                throw new InvalidDataException("Unsupported collection file version.");
            Validate(doc.Collections);
            return doc.Collections;
        }
        catch (JsonException ex) { throw new InvalidDataException("Collections could not be read; the original file has been preserved.", ex); }
    }

    private void Write(IReadOnlyList<CharacterCollection> all, string root)
    {
        Validate(all);
        // Guard every bound installation, including ones not currently selected.
        foreach (var boundRoot in all.Select(c => c.LibraryRoot).Append(root))
        {
            var normalized = NormalizeRoot(boundRoot);
            if (FilePath.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                FilePath.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Collections must be stored outside the IKEMEN installation.");
        }
        // Fail closed on linked app-data paths; a junction must not redirect a write into the engine.
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(FilePath)!); dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Collections cannot be saved through a linked directory.");
        if (File.Exists(FilePath) && File.GetAttributes(FilePath).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Collections cannot be saved through a linked file.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Document(1, all), Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool SameRoot(string a, string b) => NormalizeRoot(a).Equals(NormalizeRoot(b), StringComparison.OrdinalIgnoreCase);
    private static CharacterCollection Find(IEnumerable<CharacterCollection> all, string root, Guid id)
        => all.SingleOrDefault(c => c.Id == id && SameRoot(c.LibraryRoot, root))
           ?? throw new InvalidOperationException("The collection no longer exists in this library. Refresh and try again.");
    private static void RequireManual(CharacterCollection c)
    {
        if (c.Kind != CollectionKind.Manual) throw new InvalidOperationException("Smart collection membership is determined by its rules.");
    }
    private static void CheckName(IEnumerable<CharacterCollection> all, CharacterCollection c)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) throw new InvalidOperationException("Enter a collection name.");
        if (c.Name.Equals("All Characters", StringComparison.OrdinalIgnoreCase) || all.Any(other => other.Id != c.Id &&
            SameRoot(other.LibraryRoot, c.LibraryRoot) && other.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A collection with that name already exists.");
    }
    private static void Validate(IReadOnlyList<CharacterCollection> all)
    {
        var ids = new HashSet<Guid>();
        foreach (var c in all)
        {
            if (c is null || c.Id == Guid.Empty || !ids.Add(c.Id) || string.IsNullOrWhiteSpace(c.Name) ||
                string.IsNullOrWhiteSpace(c.LibraryRoot) || !Path.IsPathFullyQualified(c.LibraryRoot) ||
                !Enum.IsDefined(c.Kind) || !Enum.IsDefined(c.Match) || c.Members is null || c.Rules is null ||
                c.Members.Any(m => m is null || string.IsNullOrWhiteSpace(m.CharacterId) || m.DisplayName is null) ||
                c.Rules.Any(r => r is null || r.Value is null || !Enum.IsDefined(r.Field) || !Enum.IsDefined(r.Comparison)))
                throw new InvalidDataException("Invalid collection data; the original file has been preserved.");
        }
    }
}
