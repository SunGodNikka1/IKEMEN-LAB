using IKEMENLab.Core.Models;

namespace IKEMENLab.Core.Library;

/// <summary>
/// The single source of "Date Added" for characters and stages (browsers and Dashboard alike).
///
/// Once IKEMEN Lab has a record for an identity it never recomputes it from mutable file times, so
/// editing a DEF, touching files, roster toggles, thumbnails or replacing an installed item leave the
/// date alone. Records are established as:
/// <list type="bullet">
///   <item><b>Installed</b> — exact time of a successful IKEMEN Lab install;</item>
///   <item><b>FirstSeen</b> — content that appeared after tracking began (manual copies/moves), dated
///         within the window since the previous scan using file-system arrival evidence;</item>
///   <item><b>LegacyEstimate</b> — content present at the first tracked scan; estimated once from
///         file-system evidence and labelled as an estimate.</item>
/// </list>
/// Without a store (tests, read-only tools) entries get a non-persisted estimate.
/// </summary>
public sealed class DateAddedTracker
{
    private static readonly TimeSpan PruneMissingAfter = TimeSpan.FromDays(180);
    private readonly DateAddedStore? _store;
    private readonly Func<DateTime> _clockUtc;

    public DateAddedTracker(DateAddedStore? store, Func<DateTime>? clockUtc = null)
    {
        _store = store;
        _clockUtc = clockUtc ?? (() => DateTime.UtcNow);
    }

    public static DateAddedTracker EstimateOnly { get; } = new(null);

    public bool Persists => _store is not null;

    /// <summary>
    /// Annotates indexed entries with their Date Added. <paramref name="healthyScan"/> must only be true
    /// when chars/ and stages/ were readable, because it allows absent items to be marked missing.
    /// </summary>
    public (IReadOnlyList<CharacterEntry> Characters, IReadOnlyList<StageEntry> Stages) Apply(
        string root, IReadOnlyList<CharacterEntry> characters, IReadOnlyList<StageEntry> stages, bool healthyScan)
    {
        root = DateAddedStore.NormalizeRoot(root);
        var now = _clockUtc();

        var items = characters.Select(c => (Id: ContentIdentity.ForCharacter(c), Path: c.DefPath))
            .Concat(stages.Select(s => (Id: ContentIdentity.ForStage(s), Path: s.RootRelativeDefPath)))
            .ToList();

        Dictionary<string, DateAddedRecord> resolved;
        if (_store is null)
        {
            resolved = items
                .GroupBy(i => i.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new DateAddedRecord
                {
                    DateAddedUtc = Min(Arrival(root, g.Key) ?? now, now),
                    Source = DateAddedSource.LegacyEstimate,
                    RecordedUtc = now
                }, StringComparer.Ordinal);
        }
        else
        {
            resolved = _store.Update(root, doc =>
            {
                var firstScan = doc.BaselineUtc is null;
                if (firstScan) doc.BaselineUtc = now;
                var windowStart = Min(doc.LastScanUtc ?? now, now);
                var seen = new Dictionary<string, DateAddedRecord>(StringComparer.Ordinal);

                foreach (var (id, path) in items)
                {
                    if (seen.ContainsKey(id)) continue;
                    if (doc.Items.TryGetValue(id, out var record))
                    {
                        if (record.MissingSinceUtc is { } missingSince)
                        {
                            // Came back after its folder/DEF was gone: that is a new addition.
                            record = NewRecord(root, id, DateAddedSource.FirstSeen, Max(missingSince, windowStart), now);
                            doc.Items[id] = record;
                        }
                    }
                    else
                    {
                        record = firstScan
                            ? NewRecord(root, id, DateAddedSource.LegacyEstimate, DateTime.MinValue, now)
                            : NewRecord(root, id, DateAddedSource.FirstSeen, windowStart, now);
                        doc.Items[id] = record;
                    }

                    record.LastKnownPath = path;
                    seen[id] = record;
                }

                if (healthyScan)
                {
                    foreach (var (id, record) in doc.Items.ToList())
                    {
                        if (seen.ContainsKey(id)) continue;
                        // Only an absent folder/DEF counts as removal; an item that is merely not indexable
                        // right now (e.g. a DEF being edited) keeps its record untouched.
                        if (FileSystemTimes.Read(ContentIdentity.AnchorPath(root, id)) is not null) continue;
                        record.MissingSinceUtc ??= now;
                        if (now - record.MissingSinceUtc > PruneMissingAfter) doc.Items.Remove(id);
                    }
                }

                doc.LastScanUtc = now;
                return (seen, true);
            });
        }

        CharacterEntry AnnotateCharacter(CharacterEntry c) =>
            resolved.TryGetValue(ContentIdentity.ForCharacter(c), out var r)
                ? c with { DateAddedUtc = r.DateAddedUtc, DateAddedSource = r.Source }
                : c;

        StageEntry AnnotateStage(StageEntry s) =>
            resolved.TryGetValue(ContentIdentity.ForStage(s), out var r)
                ? s with { DateAddedUtc = r.DateAddedUtc, DateAddedSource = r.Source }
                : s;

        return (characters.Select(AnnotateCharacter).ToList(), stages.Select(AnnotateStage).ToList());
    }

    /// <summary>
    /// Before an install replaces existing content, makes sure that content has a record so the
    /// replacement keeps its original Date Added (the folder is recreated by the replace).
    /// </summary>
    public void EnsureRecorded(string root, IEnumerable<string> identities)
    {
        if (_store is null) return;
        root = DateAddedStore.NormalizeRoot(root);
        var now = _clockUtc();
        var ids = identities.Distinct(StringComparer.Ordinal).ToList();
        _store.Update(root, doc =>
        {
            var changed = false;
            foreach (var id in ids)
            {
                if (doc.Items.ContainsKey(id)) continue;
                if (FileSystemTimes.Read(ContentIdentity.AnchorPath(root, id)) is null) continue;
                var source = doc.BaselineUtc is null ? DateAddedSource.LegacyEstimate : DateAddedSource.FirstSeen;
                var windowStart = Min(doc.LastScanUtc ?? now, now);
                doc.Items[id] = NewRecord(root, id, source, source == DateAddedSource.FirstSeen ? windowStart : DateTime.MinValue, now);
                changed = true;
            }

            return (0, changed);
        });
    }

    /// <summary>
    /// Records a successful IKEMEN Lab install. New (or previously removed) items get the exact install
    /// time; items that already have a live record keep their original date (replacement/update).
    /// </summary>
    public void RecordInstalled(string root, IEnumerable<string> identities)
    {
        if (_store is null) return;
        root = DateAddedStore.NormalizeRoot(root);
        var now = _clockUtc();
        var ids = identities.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return;
        _store.Update(root, doc =>
        {
            var changed = false;
            foreach (var id in ids)
            {
                if (doc.Items.TryGetValue(id, out var existing) && existing.MissingSinceUtc is null) continue;
                doc.Items[id] = new DateAddedRecord
                {
                    DateAddedUtc = now,
                    Source = DateAddedSource.Installed,
                    RecordedUtc = now
                };
                changed = true;
            }

            return (0, changed);
        });
    }

    private static DateAddedRecord NewRecord(string root, string id, DateAddedSource source, DateTime notBefore, DateTime now)
    {
        var estimate = Arrival(root, id) ?? now;
        return new DateAddedRecord
        {
            DateAddedUtc = Min(Max(estimate, notBefore), now),
            Source = source,
            RecordedUtc = now
        };
    }

    private static DateTime? Arrival(string root, string id)
        => FileSystemTimes.ArrivalEstimateUtc(ContentIdentity.AnchorPath(root, id));

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
