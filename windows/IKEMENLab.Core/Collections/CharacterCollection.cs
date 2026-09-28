using IKEMENLab.Core.Models;

namespace IKEMENLab.Core.Collections;

public enum CollectionKind { Manual, Smart }
public enum RuleMatch { All, Any }
public enum RuleField { Name, Author, Folder, Registration }
public enum RuleComparison { Contains, DoesNotContain, Equals, NotEquals, IsEmpty, IsNotEmpty }

public sealed record CollectionRule(RuleField Field, RuleComparison Comparison, string Value);
public sealed record CollectionMember(string CharacterId, string DisplayName);

public sealed record CharacterCollection
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string LibraryRoot { get; init; }
    public CollectionKind Kind { get; init; }
    public RuleMatch Match { get; init; }
    public IReadOnlyList<CollectionRule> Rules { get; init; } = [];
    public IReadOnlyList<CollectionMember> Members { get; init; } = [];
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record ResolvedMember(CollectionMember Member, CharacterEntry? Character)
{
    public bool IsMissing => Character is null;
}

public static class CollectionMembership
{
    public static string Key(string id) => id.Replace('\\', '/').Trim('/');

    public static IReadOnlyList<ResolvedMember> Resolve(CharacterCollection collection, IReadOnlyList<CharacterEntry> index)
    {
        var entries = index.GroupBy(c => Key(c.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (collection.Kind == CollectionKind.Smart)
            return entries.Values.Where(c => SmartCollectionEvaluator.Matches(collection.Rules, collection.Match, c))
                .Select(c => new ResolvedMember(new(c.Id, c.DisplayName), c)).ToArray();
        return collection.Members.Select(m => new ResolvedMember(m, entries.GetValueOrDefault(Key(m.CharacterId)))).ToArray();
    }
}
