using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Names;

/// <summary>Where the label shown for an object came from.</summary>
public enum NameSource
{
    /// <summary>X-Ray's own name (State 200, the command name, Action 200 …).</summary>
    XRay,

    /// <summary>The user's name for this object.</summary>
    User,

    /// <summary>The user's name for the ability this state is the entry of (or for the entry state of this ability).</summary>
    Linked
}

/// <summary>State of a saved name against the current files.</summary>
public enum NameStatus
{
    /// <summary>The object exists and its fingerprint still matches: the name is shown.</summary>
    Current,

    /// <summary>The object exists but changed: the name is NOT shown until the user confirms, moves or discards it.</summary>
    Stale,

    /// <summary>The object no longer exists: the name waits for the user to re-attach or discard it.</summary>
    Orphaned
}

/// <summary>A saved name that is not applied, with possible new homes (same kind and fingerprint, not yet named). Never auto-applied.</summary>
public sealed record NameReviewItem(string Id, string Label, string Kind, NameStatus Status, string DefaultNameAtRename, string? CurrentDefaultName, IReadOnlyList<string> Suggestions);

/// <summary>
/// The one display-name resolver for a <see cref="SemanticIndex"/>. Every surface (lenses, combo lens, traces, diagnostics, CLI JSON)
/// asks it for <see cref="Display"/>; raw ids and X-Ray's own names stay available through <see cref="Default"/> and
/// <see cref="WithTechnical"/>. A name is applied only while its fingerprint matches; a changed or missing object puts it in
/// <see cref="Review"/> instead of guessing.
/// </summary>
public sealed class SemanticNames
{
    public const int MaxLabelLength = 60;

    private readonly Dictionary<string, NameRecord> _records;
    private readonly Dictionary<string, NameStatus> _status = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _entryToAbility = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _abilityToEntry = new(StringComparer.Ordinal);

    public SemanticNames(SemanticIndex index, IEnumerable<KeyValuePair<string, NameRecord>> records)
    {
        Index = index;
        _records = records.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var a in index.Of(ObjectKind.Ability))
            if (a.Prop("entryState") is { } entry && index.Get(entry) is not null)
            {
                _entryToAbility.TryAdd(entry, a.Id);
                _abilityToEntry[a.Id] = entry;
            }

        foreach (var (id, rec) in _records)
        {
            var fp = NameFingerprint.Compute(index, id);
            _status[id] = fp is null ? NameStatus.Orphaned : fp == rec.Fingerprint ? NameStatus.Current : NameStatus.Stale;
        }
    }

    public static SemanticNames Empty(SemanticIndex index) => new(index, []);

    public SemanticIndex Index { get; }

    /// <summary>Saved records (applied or not), keyed by object id.</summary>
    public IReadOnlyDictionary<string, NameRecord> Records => _records;

    public bool HasNames => _records.Count > 0;

    /// <summary>X-Ray's own name (technical label), or the id itself for an unknown id.</summary>
    public string Default(string id) => Index.Get(id)?.Name ?? id;

    public NameStatus? StatusOf(string id) => _status.TryGetValue(id, out var s) ? s : null;

    public bool CanRename(string id) => NameFingerprint.IsRenameable(Index.Get(id));

    /// <summary>The label every surface shows for <paramref name="id"/>.</summary>
    public string Display(string id) => Resolve(id).Label;

    public NameSource SourceOf(string id) => Resolve(id).Source;

    public bool IsRenamed(string id) => SourceOf(id) != NameSource.XRay;

    /// <summary>The user-facing name of runtime state <paramref name="stateNo"/> when it carries a user name, else null (traces keep the number).</summary>
    public string? RenamedState(int? stateNo) =>
        stateNo is { } n && $"state:{n}" is var id && Index.Get(id) is not null && IsRenamed(id) ? Display(id) : null;

    /// <summary>"Revolver Shot · State 66345" when renamed; otherwise X-Ray's name. For text where the raw identity must stay visible.</summary>
    public string WithTechnical(string id) => IsRenamed(id) ? $"{Display(id)} · {Default(id)}" : Default(id);

    private (string Label, NameSource Source) Resolve(string id)
    {
        if (Current(id) is { } own) return (own.Label, NameSource.User);
        if (_entryToAbility.TryGetValue(id, out var ability) && Current(ability) is { } a) return (a.Label, NameSource.Linked);
        if (_abilityToEntry.TryGetValue(id, out var entry) && Current(entry) is { } s) return (s.Label, NameSource.Linked);
        return (Default(id), NameSource.XRay);
    }

    private NameRecord? Current(string id) =>
        _records.TryGetValue(id, out var r) && _status.TryGetValue(id, out var st) && st == NameStatus.Current ? r : null;

    /// <summary>Names that are saved but not shown (changed or missing objects), each with suggestions for a new home.</summary>
    public IReadOnlyList<NameReviewItem> Review()
    {
        var named = _records.Keys.ToHashSet(StringComparer.Ordinal);
        var items = new List<NameReviewItem>();
        foreach (var (id, rec) in _records.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var st = _status[id];
            if (st == NameStatus.Current) continue;
            var suggestions = Index.Objects
                .Where(o => o.Kind.ToString() == rec.Kind && !named.Contains(o.Id) && NameFingerprint.IsRenameable(o))
                .Where(o => NameFingerprint.Compute(Index, o.Id) == rec.Fingerprint)
                .Select(o => o.Id).Take(5).ToList();
            items.Add(new NameReviewItem(id, rec.Label, rec.Kind, st, rec.DefaultName, Index.Get(id)?.Name, suggestions));
        }

        return items;
    }

    public static string? ValidateLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "The name is empty.";
        var t = label.Trim();
        if (t.Length > MaxLabelLength) return $"Use at most {MaxLabelLength} characters.";
        if (t.Any(char.IsControl)) return "The name contains control characters.";
        return null;
    }

    /// <summary>A record for <paramref name="id"/> as it is now (label + current fingerprint).</summary>
    internal NameRecord NewRecord(string id, string label, DateTime nowUtc)
    {
        var o = Index.Get(id) ?? throw new ArgumentException("Unknown id: " + id, nameof(id));
        return new NameRecord
        {
            Label = label.Trim(), Kind = o.Kind.ToString(), Fingerprint = NameFingerprint.Compute(Index, id)!, DefaultName = o.Name, RenamedUtc = nowUtc
        };
    }
}
