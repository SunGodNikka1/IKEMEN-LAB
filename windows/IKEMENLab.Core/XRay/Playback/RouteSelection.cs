namespace IKEMENLab.Core.XRay.Playback;

/// <summary>
/// Unambiguous ways to pick one candidate route (or any row) from a list: by 1-based index, by exact key, or by a substring that must match exactly one.
/// Runtime evidence must name exactly which candidate was played, so nothing here ever silently picks "the first match".
/// </summary>
public static class RouteSelection
{
    /// <summary>1-based, as in <c>--route N</c>. Returns the 0-based position.</summary>
    public static int ByIndex(int count, int oneBased)
    {
        if (count == 0) throw new InvalidOperationException("There are no candidate routes; run the route search first.");
        if (oneBased < 1 || oneBased > count) throw new InvalidOperationException($"Route index {oneBased} is out of range; there are {count} candidate route(s) (1..{count}).");
        return oneBased - 1;
    }

    /// <summary>Exact, ordinal, case-sensitive key match. Returns the 0-based position.</summary>
    public static int ByKey(IReadOnlyList<string> keys, string exactKey)
    {
        var hits = Enumerable.Range(0, keys.Count).Where(i => string.Equals(keys[i], exactKey, StringComparison.Ordinal)).ToList();
        if (hits.Count == 1) return hits[0];
        throw new InvalidOperationException(hits.Count == 0
            ? $"No route has the exact key '{exactKey}' among {keys.Count}."
            : $"The key '{exactKey}' is not unique ({hits.Count} routes share it).");
    }

    /// <summary>Case-insensitive substring match that must identify exactly one row; ambiguity is an error that lists the candidates.</summary>
    public static int BySubstring(IReadOnlyList<string> keys, string needle)
    {
        var hits = Enumerable.Range(0, keys.Count).Where(i => keys[i].Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0) throw new InvalidOperationException($"No row matches '{needle}' among {keys.Count}.");
        throw new InvalidOperationException(
            $"'{needle}' matches {hits.Count} rows, so it does not identify one. Use an exact selector (index or key). Matches: " +
            string.Join("; ", hits.Take(6).Select(i => $"#{i + 1} {keys[i]}")) + (hits.Count > 6 ? "; …" : string.Empty));
    }
}
