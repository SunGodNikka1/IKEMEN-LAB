namespace IKEMENLab.Core.Services;

/// <summary>
/// Which row a browser selects after its list is rebuilt from a new library snapshot (Refresh, a roster
/// toggle, an install or a delete). Items are matched by library id, because a rebuild creates new rows.
/// </summary>
public static class BrowserSelection
{
    /// <summary>
    /// Keeps the previously selected item when it is still listed. When it is gone, nothing is selected
    /// rather than jumping to an unrelated item. Without a previous selection (first load, another
    /// installation) the first item is chosen when <paramref name="selectFirstWhenNone"/> is set.
    /// </summary>
    public static T? AfterRefresh<T>(IReadOnlyList<T> visible, Func<T, string> id, string? previousId, bool selectFirstWhenNone)
        where T : class
    {
        if (previousId is null) return selectFirstWhenNone ? visible.FirstOrDefault() : null;
        return visible.FirstOrDefault(item => string.Equals(id(item), previousId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The neighbour to select once <paramref name="removedId"/> leaves the list: the next item, or the
    /// previous one when the removed item was last. Null when it was the only item (or is not listed).
    /// </summary>
    public static string? AdjacentId<T>(IReadOnlyList<T> visible, Func<T, string> id, string removedId)
    {
        var index = -1;
        for (var i = 0; i < visible.Count; i++)
        {
            if (!string.Equals(id(visible[i]), removedId, StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            break;
        }

        if (index < 0) return null;
        if (index + 1 < visible.Count) return id(visible[index + 1]);
        return index > 0 ? id(visible[index - 1]) : null;
    }
}
