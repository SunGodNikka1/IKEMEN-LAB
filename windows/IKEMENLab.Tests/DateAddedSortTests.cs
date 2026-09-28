using IKEMENLab.Core.Library;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public sealed class DateAddedSortTests
{
    private sealed record Item(string Name, DateTime? DateAddedUtc);

    private static List<Item> Sample() =>
    [
        new("Charlie", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        new("Alpha", new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc)),
        new("Bravo", new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc)),
        new("Zulu", null),
        new("NullTwo", null),
        new("SameDayB", new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc)),
        new("SameDayA", new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc)),
    ];

    private static List<string> Names(IEnumerable<Item> items) => items.Select(i => i.Name).ToList();

    [Fact]
    public void LatestAdded_orders_descending_with_name_tiebreak()
    {
        var ordered = DateAddedSort.Apply(Sample(), BrowserSortMode.LatestAdded, i => i.DateAddedUtc, i => i.Name).ToList();
        Assert.Equal(
            ["Alpha", "SameDayA", "SameDayB", "Bravo", "Charlie", "NullTwo", "Zulu"],
            Names(ordered));
    }

    [Fact]
    public void OldestAdded_orders_ascending_with_name_tiebreak()
    {
        var ordered = DateAddedSort.Apply(Sample(), BrowserSortMode.OldestAdded, i => i.DateAddedUtc, i => i.Name).ToList();
        Assert.Equal(
            ["Charlie", "Bravo", "Alpha", "SameDayA", "SameDayB", "NullTwo", "Zulu"],
            Names(ordered));
    }

    [Fact]
    public void Null_dates_always_last_for_latest_and_oldest()
    {
        var items = new List<Item>
        {
            new("NullFirst", null),
            new("Old", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            new("New", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            new("NullSecond", null),
        };

        var latest = Names(DateAddedSort.Apply(items, BrowserSortMode.LatestAdded, i => i.DateAddedUtc, i => i.Name));
        var oldest = Names(DateAddedSort.Apply(items, BrowserSortMode.OldestAdded, i => i.DateAddedUtc, i => i.Name));

        Assert.Equal(["New", "Old", "NullFirst", "NullSecond"], latest);
        Assert.Equal(["Old", "New", "NullFirst", "NullSecond"], oldest);
        Assert.Equal("NullFirst", latest[^2]);
        Assert.Equal("NullSecond", latest[^1]);
        Assert.Equal("NullFirst", oldest[^2]);
        Assert.Equal("NullSecond", oldest[^1]);
    }

    [Fact]
    public void Equal_timestamp_uses_name_ascending_tiebreak()
    {
        var items = new List<Item>
        {
            new("Zed", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
            new("Ada", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
            new("Mia", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
        };

        Assert.Equal(["Ada", "Mia", "Zed"], Names(DateAddedSort.Apply(items, BrowserSortMode.LatestAdded, i => i.DateAddedUtc, i => i.Name)));
        Assert.Equal(["Ada", "Mia", "Zed"], Names(DateAddedSort.Apply(items, BrowserSortMode.OldestAdded, i => i.DateAddedUtc, i => i.Name)));
    }

    [Fact]
    public void Default_preserves_original_ordering()
    {
        var sample = Sample();
        var ordered = DateAddedSort.Apply(sample, BrowserSortMode.Default, i => i.DateAddedUtc, i => i.Name).ToList();
        Assert.Equal(Names(sample), Names(ordered));
    }

    [Fact]
    public void Search_then_sort_does_not_drop_entries()
    {
        var all = Sample();
        var filtered = all.Where(i => i.Name.Contains("a", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(filtered, i => i.Name == "Alpha");
        Assert.Contains(filtered, i => i.Name == "SameDayA");
        Assert.Contains(filtered, i => i.Name == "Charlie");

        var latest = DateAddedSort.Apply(filtered, BrowserSortMode.LatestAdded, i => i.DateAddedUtc, i => i.Name).ToList();
        var oldest = DateAddedSort.Apply(filtered, BrowserSortMode.OldestAdded, i => i.DateAddedUtc, i => i.Name).ToList();
        var againDefault = DateAddedSort.Apply(filtered, BrowserSortMode.Default, i => i.DateAddedUtc, i => i.Name).ToList();

        Assert.Equal(filtered.Count, latest.Count);
        Assert.Equal(filtered.Count, oldest.Count);
        Assert.Equal(filtered.Count, againDefault.Count);
        Assert.Equal(filtered.Select(i => i.Name).OrderBy(n => n).ToList(),
            latest.Select(i => i.Name).OrderBy(n => n).ToList());
        Assert.Equal(Names(filtered), Names(againDefault));
    }

    [Fact]
    public void Switching_sort_modes_preserves_membership()
    {
        var sample = Sample();
        var modes = new[] { BrowserSortMode.Default, BrowserSortMode.LatestAdded, BrowserSortMode.OldestAdded, BrowserSortMode.Default };
        HashSet<string>? expected = null;
        foreach (var mode in modes)
        {
            var ordered = DateAddedSort.Apply(sample, mode, i => i.DateAddedUtc, i => i.Name).ToList();
            var names = ordered.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            expected ??= names;
            Assert.Equal(expected, names);
            Assert.Equal(sample.Count, ordered.Count);
        }
    }

    [Fact]
    public void FormatAddedLabel_null_when_missing_and_local_when_present()
    {
        Assert.Null(DateAddedSort.FormatAddedLabel(null));
        var utc = new DateTime(2026, 9, 27, 18, 0, 0, DateTimeKind.Utc);
        var label = DateAddedSort.FormatAddedLabel(utc);
        Assert.NotNull(label);
        Assert.StartsWith("Added ", label);
        Assert.Contains("2026", label);
    }

    [Fact]
    public void Column_form_marks_estimates_and_never_invents_a_date()
    {
        var utc = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Null(DateAddedSort.FormatAddedShort(null, DateAddedSource.Installed));
        Assert.Equal(local, DateAddedSort.FormatAddedShort(utc, DateAddedSource.Installed));
        Assert.Equal(local, DateAddedSort.FormatAddedShort(utc, DateAddedSource.FirstSeen));
        Assert.Equal("Est. " + local, DateAddedSort.FormatAddedShort(utc, DateAddedSource.LegacyEstimate));
        Assert.Equal("Estimated added " + local, DateAddedSort.FormatAddedLabel(utc, DateAddedSource.LegacyEstimate));
    }

    [Fact]
    public void Character_and_stage_entry_sort_use_DateAddedUtc_not_ModifiedAtUtc()
    {
        var stages = new[]
        {
            new { Name = "NewInstallOldDef", DateAddedUtc = (DateTime?)new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), ModifiedAtUtc = (DateTime?)new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new { Name = "OldInstallNewDef", DateAddedUtc = (DateTime?)new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), ModifiedAtUtc = (DateTime?)new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc) },
            new { Name = "UnknownInstall", DateAddedUtc = (DateTime?)null, ModifiedAtUtc = (DateTime?)new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc) },
        };

        var latest = DateAddedSort.Apply(stages, BrowserSortMode.LatestAdded, s => s.DateAddedUtc, s => s.Name)
            .Select(s => s.Name).ToList();
        Assert.Equal(["NewInstallOldDef", "OldInstallNewDef", "UnknownInstall"], latest);

        var chars = new[]
        {
            new { DisplayName = "KFM", DateAddedUtc = (DateTime?)new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new { DisplayName = "Newer", DateAddedUtc = (DateTime?)new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) },
        };
        var charLatest = DateAddedSort.Apply(chars, BrowserSortMode.LatestAdded, c => c.DateAddedUtc, c => c.DisplayName)
            .Select(c => c.DisplayName).ToList();
        Assert.Equal(["Newer", "KFM"], charLatest);
    }
}
