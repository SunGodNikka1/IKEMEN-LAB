using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Model;

public sealed record ExplainItem(string Id, string Name, ObjectKind Kind, Confidence? Confidence, string? Via, SourceRef? Source, string? Note, string? Rule = null);

public sealed record ExplainSection(string Title, IReadOnlyList<ExplainItem> Items);

public sealed record Explanation(SemanticObject Subject, IReadOnlyList<ExplainSection> Sections);

/// <summary>A ChangesState edge seen as a transition between states. Milestone 2 turns these into combo candidates.</summary>
public sealed record Transition(string FromState, string ToState, string ControllerId, RelationKind Kind, Confidence Confidence, Gate? Gate, string RelationshipId);

public sealed record VarUse(Relationship Relationship, SemanticObject Controller, SemanticObject? State);

/// <summary>
/// The Character Semantic Index: every object and relationship found in one character's files, with provenance and
/// evidence confidence. UI-agnostic and immutable once built; all views and the CLI query this one structure.
/// </summary>
public sealed partial class SemanticIndex
{
    private readonly Dictionary<string, SemanticObject> _byId;
    private readonly Dictionary<string, List<Relationship>> _out = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Relationship>> _in = new(StringComparer.Ordinal);

    public const string SchemaVersion = "ikemenlab.xray/1";

    public SemanticIndex(string characterId, IReadOnlyList<SourceFile> files, IReadOnlyList<SemanticObject> objects,
        IReadOnlyList<Relationship> relationships, IReadOnlyList<Diagnostic> diagnostics)
    {
        CharacterId = characterId;
        Files = files;
        Objects = objects;
        Relationships = relationships;
        Diagnostics = diagnostics;
        _byId = objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
        foreach (var r in relationships)
        {
            (_out.TryGetValue(r.From, out var o) ? o : _out[r.From] = []).Add(r);
            (_in.TryGetValue(r.To, out var i) ? i : _in[r.To] = []).Add(r);
        }
    }

    public string CharacterId { get; }
    public IReadOnlyList<SourceFile> Files { get; }
    public IReadOnlyList<SemanticObject> Objects { get; }
    public IReadOnlyList<Relationship> Relationships { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public SemanticObject? Get(string id) => _byId.TryGetValue(id, out var o) ? o : null;

    private SemanticNames? _names;

    /// <summary>
    /// The display-name resolver (user names over X-Ray's own names). Empty until <see cref="UseNames"/> attaches a character's
    /// saved names. Names are presentation only: they never change ids, relationships, evidence or confidence.
    /// </summary>
    public SemanticNames Names => _names ??= SemanticNames.Empty(this);

    public void UseNames(SemanticNames names)
    {
        if (!ReferenceEquals(names.Index, this)) throw new ArgumentException("The names were resolved against another index.", nameof(names));
        _names = names;
    }

    /// <summary>The label to show for <paramref name="id"/>: the user's name when one applies, else X-Ray's own name, else the id.</summary>
    public string NameOf(string id) => Names.Display(id);

    public IEnumerable<SemanticObject> Of(ObjectKind kind) => Objects.Where(o => o.Kind == kind);

    public IReadOnlyList<Relationship> Outgoing(string id, params RelationKind[] kinds) =>
        Filter(_out.TryGetValue(id, out var l) ? l : null, kinds);

    public IReadOnlyList<Relationship> Incoming(string id, params RelationKind[] kinds) =>
        Filter(_in.TryGetValue(id, out var l) ? l : null, kinds);

    private static IReadOnlyList<Relationship> Filter(List<Relationship>? list, RelationKind[] kinds) =>
        list is null ? [] : kinds.Length == 0 ? list : list.Where(r => kinds.Contains(r.Kind)).ToList();

    public SourceFile? FileOf(SourceRef? source) =>
        source is { } s && s.FileId >= 0 && s.FileId < Files.Count ? Files[s.FileId] : null;

    /// <summary>Every id within <paramref name="depth"/> relationship hops of <paramref name="id"/>, in either direction.</summary>
    public IReadOnlyCollection<string> Neighborhood(string id, int depth, params RelationKind[] kinds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var frontier = new List<string> { id };
        for (var d = 0; d < depth && frontier.Count > 0; d++)
        {
            var next = new List<string>();
            foreach (var cur in frontier)
            {
                foreach (var r in Outgoing(cur, kinds)) if (seen.Add(r.To)) next.Add(r.To);
                foreach (var r in Incoming(cur, kinds)) if (seen.Add(r.From)) next.Add(r.From);
            }

            frontier = next;
        }

        return seen;
    }

    public IReadOnlyList<SemanticObject> Search(string text, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var q = text.Trim();
        return Objects
            .Where(o => o.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        o.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (_names is not null && NameOf(o.Id).Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                        o.Labels.Any(l => l.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();
    }

    public IReadOnlyList<SemanticObject> FindByLabel(string category) =>
        Objects.Where(o => o.Labels.Any(l => l.Category.Equals(category, StringComparison.OrdinalIgnoreCase))).ToList();

    public static string GateText(Gate? gate)
    {
        if (gate is null || gate.IsEmpty) return "(no triggers)";
        var parts = new List<string>();
        foreach (var l in gate.TriggerAll) parts.Add("triggerall: " + l.Text);
        foreach (var branch in gate.Branches)
            parts.Add($"trigger{branch.Number}: " + string.Join(" AND ", branch.Lines.Select(l => l.Text)));
        return string.Join(" | ", parts);
    }
}
