using IKEMENLab.Core.Library;

namespace IKEMENLab.Core.Services;

/// <summary>Browser sort modes for Characters / Stages, ordered by <see cref="DateAddedTracker"/> dates.</summary>
public enum BrowserSortMode
{
    /// <summary>Preserve indexer / snapshot order.</summary>
    Default = 0,
    /// <summary>DateAddedUtc descending; nulls last; name ascending tie-break.</summary>
    LatestAdded = 1,
    /// <summary>DateAddedUtc ascending; nulls last; name ascending tie-break.</summary>
    OldestAdded = 2
}

public sealed record BrowserSortOption(BrowserSortMode Mode, string Label);

/// <summary>Shared Date Added ordering for the character and stage browsers.</summary>
public static class DateAddedSort
{
    public static IReadOnlyList<BrowserSortOption> Options { get; } =
    [
        new(BrowserSortMode.Default, "Default"),
        new(BrowserSortMode.LatestAdded, "Latest Added"),
        new(BrowserSortMode.OldestAdded, "Oldest Added")
    ];

    /// <summary>
    /// Orders <paramref name="items"/> by date-added. Default returns items in the same sequence
    /// as enumerated (callers should pass the already-stable index order).
    /// Null <paramref name="installedAtUtc"/> values always sort last for Latest and Oldest.
    /// </summary>
    public static IEnumerable<T> Apply<T>(
        IEnumerable<T> items,
        BrowserSortMode mode,
        Func<T, DateTime?> installedAtUtc,
        Func<T, string> displayName,
        Func<T, string>? identity = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(installedAtUtc);
        ArgumentNullException.ThrowIfNull(displayName);

        if (mode == BrowserSortMode.Default)
            return items;

        var material = items as IList<T> ?? items.ToList();
        return mode switch
        {
            BrowserSortMode.LatestAdded => material
                .OrderBy(i => installedAtUtc(i) is null ? 1 : 0)
                .ThenByDescending(i => installedAtUtc(i) ?? DateTime.MinValue)
                .ThenBy(i => displayName(i) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => identity?.Invoke(i) ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            BrowserSortMode.OldestAdded => material
                .OrderBy(i => installedAtUtc(i) is null ? 1 : 0)
                .ThenBy(i => installedAtUtc(i) ?? DateTime.MaxValue)
                .ThenBy(i => displayName(i) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => identity?.Invoke(i) ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            _ => material
        };
    }

    /// <summary>
    /// "Added Sep 27, 2026" in local time, or "Estimated added Sep 20, 2026" for content that predates
    /// tracking (its exact add time is unknowable). Null when no date is known.
    /// </summary>
    public static string? FormatAddedLabel(DateTime? installedAtUtc, DateAddedSource? source = null)
    {
        if (installedAtUtc is not { } utc) return null;
        var local = utc.ToLocalTime();
        var date = local.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return source == DateAddedSource.LegacyEstimate ? "Estimated added " + date : "Added " + date;
    }

    /// <summary>Compact column form: "Sep 27, 2026", or "Est. Sep 27, 2026" for a legacy estimate.</summary>
    public static string? FormatAddedShort(DateTime? installedAtUtc, DateAddedSource? source = null)
    {
        if (installedAtUtc is not { } utc) return null;
        var date = utc.ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return source == DateAddedSource.LegacyEstimate ? "Est. " + date : date;
    }
}
