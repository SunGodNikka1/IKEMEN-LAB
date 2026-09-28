using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;

namespace IKEMENLab.Core.Services;

public enum RecentContentType
{
    Character,
    Stage
}

public sealed record RecentContentItem(
    string Id,
    string Name,
    string Author,
    RecentContentType Type,
    DateTime DateAddedUtc,
    ContentStatus Status,
    string DefPath,
    DateAddedSource? DateAddedSource = null,
    bool NeedsDefChoice = false)
{
    public bool IsEstimated => DateAddedSource == Library.DateAddedSource.LegacyEstimate;
}

/// <summary>
/// Dashboard "Recently Installed": the newest characters and stages by the same Date Added that the
/// browsers sort on (<see cref="DateAddedTracker"/>), so there is one definition of "recent".
/// </summary>
public static class RecentContent
{
    public static IReadOnlyList<RecentContentItem> FromSnapshot(LibrarySnapshot snapshot, int limit = 10)
    {
        var characters = snapshot.Characters
            .Where(c => c.DateAddedUtc is not null)
            .Select(c => new RecentContentItem(
                c.Id, c.DisplayName, c.Author, RecentContentType.Character,
                c.DateAddedUtc!.Value, c.Status, c.DefPath, c.DateAddedSource, c.NeedsDefChoice));

        var stages = snapshot.Stages
            .Where(s => s.DateAddedUtc is not null)
            .Select(s => new RecentContentItem(
                s.Id, s.Name, s.Author, RecentContentType.Stage,
                s.DateAddedUtc!.Value, s.Status, s.RootRelativeDefPath, s.DateAddedSource));

        return characters.Concat(stages)
            .OrderByDescending(i => i.DateAddedUtc)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.DefPath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>macOS RecentInstallRow format: "Today, 3:04 PM", "Yesterday", "3 days ago", "Mar 7".</summary>
    public static string FormatInstallDate(DateTime utc, DateTime nowLocal)
    {
        var local = utc.ToLocalTime();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (local.Date == nowLocal.Date) return "Today, " + local.ToString("h:mm tt", culture);
        if (local.Date == nowLocal.Date.AddDays(-1)) return "Yesterday";

        var days = (int)(nowLocal.Date - local.Date).TotalDays;
        if (days is > 1 and < 7) return $"{days} days ago";
        return local.Year == nowLocal.Year
            ? local.ToString("MMM d", culture)
            : local.ToString("MMM d, yyyy", culture);
    }

    /// <summary>macOS StageBrowserView relative age: "Just now", "5h ago", "Yesterday", "3d ago", "2w ago", "36mo ago".</summary>
    public static string FormatRelativeAge(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - utc;
        if (span.TotalHours < 1) return "Just now";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 2) return "Yesterday";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)}w ago";
        return $"{(int)(span.TotalDays / 30)}mo ago";
    }
}
