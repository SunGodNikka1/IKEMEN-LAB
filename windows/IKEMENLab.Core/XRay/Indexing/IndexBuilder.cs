using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Indexing;

/// <summary>Mutable accumulator the indexers write into; <see cref="Build"/> freezes it into a <see cref="SemanticIndex"/>.</summary>
public sealed class IndexBuilder
{
    private readonly SortedDictionary<string, SemanticObject> _objects = new(StringComparer.Ordinal);
    private readonly List<Relationship> _relationships = [];
    private readonly Dictionary<string, int> _relCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Relationship>> _from = new(StringComparer.Ordinal);
    private readonly HashSet<string> _partOf = new(StringComparer.Ordinal);
    private static readonly IReadOnlyList<Relationship> NoRelationships = [];

    public List<SourceFile> Files { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
    public IReadOnlyDictionary<string, SemanticObject> Objects => _objects;
    public IReadOnlyList<Relationship> Relationships => _relationships;

    public SourceFile AddFile(string relPath, SourceRole role, bool isCommon, string hash)
    {
        var file = new SourceFile(Files.Count, relPath, role, isCommon, hash);
        Files.Add(file);
        return file;
    }

    public SemanticObject Add(ObjectKind kind, string id, string name, SourceRef? source = null, string? parent = null)
    {
        if (_objects.TryGetValue(id, out var existing)) return existing;
        var obj = new SemanticObject { Id = id, Kind = kind, Name = name, Source = source, ParentId = parent };
        _objects[id] = obj;
        return obj;
    }

    public SemanticObject? Find(string id) => _objects.TryGetValue(id, out var o) ? o : null;

    /// <summary>An object the character refers to but does not define (missing state, undefined command…).</summary>
    public SemanticObject Stub(ObjectKind kind, string id, string name, string why)
    {
        var obj = Add(kind, id, name);
        if (obj.Props.Count == 0 || !obj.Props.ContainsKey("unresolved")) obj.Props.TryAdd("unresolved", why);
        return obj;
    }

    /// <summary>
    /// Adds a relationship whose confidence is dictated by <paramref name="ruleId"/>, so a heuristic can never be
    /// recorded as StaticProven by accident.
    /// </summary>
    public Relationship Relate(RelationKind kind, string from, string to, string ruleId, SourceRef? source, Gate? gate = null,
        string? note = null, IEnumerable<KeyValuePair<string, string>>? props = null)
    {
        var confidence = EvidenceRules.ConfidenceOf(ruleId);
        var stem = $"rel:{kind}:{from}>{to}";
        _relCounters[stem] = _relCounters.TryGetValue(stem, out var n) ? n + 1 : 1;
        var rel = new Relationship
        {
            Id = _relCounters[stem] == 1 ? stem : $"{stem}#{_relCounters[stem]}",
            Kind = kind,
            From = from,
            To = to,
            Confidence = confidence,
            Gate = gate
        };
        rel.Evidence.Add(new Evidence(ruleId, source, note));
        if (props is not null)
            foreach (var kv in props) rel.Props[kv.Key] = kv.Value;
        _relationships.Add(rel);
        (_from.TryGetValue(from, out var list) ? list : _from[from] = []).Add(rel);
        return rel;
    }

    /// <summary>Relationships leaving <paramref name="id"/>, in creation order (indexed; safe inside loops).</summary>
    public IReadOnlyList<Relationship> From(string id) => _from.TryGetValue(id, out var l) ? l : NoRelationships;

    /// <summary>Adds PartOf once per (member, ability).</summary>
    public void RelatePartOf(string member, string ability, string ruleId, SourceRef? source)
    {
        if (_partOf.Add(member + ">" + ability)) Relate(RelationKind.PartOf, member, ability, ruleId, source);
    }

    public void Label(SemanticObject obj, string text, string category, string ruleId)
    {
        if (obj.Labels.Any(l => l.Text == text && l.Category == category)) return;
        obj.Labels.Add(new Label(text, category, EvidenceRules.ConfidenceOf(ruleId), ruleId));
    }

    public void Warn(string code, string message, SourceRef? source, DiagnosticSeverity severity = DiagnosticSeverity.Warning) =>
        Diagnostics.Add(new Diagnostic(severity, code, message, source));

    public SemanticIndex Build(string characterId) =>
        new(characterId, Files, _objects.Values.ToList(), _relationships, Diagnostics);
}
