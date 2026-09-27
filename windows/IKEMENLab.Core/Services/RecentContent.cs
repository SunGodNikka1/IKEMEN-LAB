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
    DateTime InstalledAtUtc,
    ContentStatus Status,
    string DefPath);

/// <summary>
/// "Recently installed" derived from the filesystem: character folders and stage DEFs ordered by
/// creation time. Windows sets creation time when content is copied or extracted, so this tracks
/// install time without any database or write.
/// </summary>
public static class RecentContent
{
    public static IReadOnlyList<RecentContentItem> FromSnapshot(LibrarySnapshot snapshot, int limit = 10)
    {
        var characters = snapshot.Characters
            .Where(c => c.InstalledAtUtc is not null)
            .Select(c => new RecentContentItem(
                c.Id, c.DisplayName, c.Author, RecentContentType.Character,
                c.InstalledAtUtc!.Value, c.Status, c.DefPath));

        var stages = snapshot.Stages
            .Where(s => s.InstalledAtUtc is not null)
            .Select(s => new RecentContentItem(
                s.Id, s.Name, s.Author, RecentContentType.Stage,
                s.InstalledAtUtc!.Value, s.Status, s.RootRelativeDefPath));

        return characters.Concat(stages)
            .OrderByDescending(i => i.InstalledAtUtc)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
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
