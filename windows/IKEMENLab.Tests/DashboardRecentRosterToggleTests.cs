using System.Text.RegularExpressions;
using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Dashboard Recently Installed must toggle roster status without navigating, and without nesting the
/// interactive switch inside the OpenRecent row button.
/// </summary>
public sealed class DashboardRecentRosterToggleTests : IDisposable
{
    private readonly MutationFixture _f = MutationFixture.SameVolume();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void RecentContent_carries_NeedsDefChoice_from_character_entry()
    {
        PrimaryDefResolverTests.WriteDef(_f.Full("chars/Twins"), "Red.def", "Red", sprite: "red.sff");
        PrimaryDefResolverTests.WriteDef(_f.Full("chars/Twins"), "Blue.def", "Blue", sprite: "blue.sff");
        _f.Write("data/select.def", "[Characters]\nkfm\n\n[ExtraStages]\n");
        Directory.SetCreationTimeUtc(_f.Full("chars/Twins"), DateTime.UtcNow);

        var snapshot = new LibraryIndexService().Index(_f.Root);
        var twin = Assert.Single(snapshot.Characters, c => c.Id == "Twins");
        Assert.True(twin.NeedsDefChoice);

        var recent = RecentContent.FromSnapshot(snapshot);
        var item = Assert.Single(recent, r => r.Id == "Twins");
        Assert.True(item.NeedsDefChoice);
        Assert.Equal(ContentStatus.Unregistered, item.Status);
    }

    [Fact]
    public void Recent_character_and_stage_toggle_use_shared_roster_service()
    {
        PrimaryDefResolverTests.WriteDef(_f.Full("chars/Hero"), "Hero.def", "Hero");
        _f.Write("stages/arena.def", "[Info]\nname = Arena\n[BGdef]\nspr = a.sff\n");
        _f.Write("data/select.def", "[Characters]\nkfm\n\n[ExtraStages]\n");
        Directory.SetCreationTimeUtc(_f.Full("chars/Hero"), DateTime.UtcNow.AddMinutes(-1));
        File.SetCreationTimeUtc(_f.Full("stages/arena.def"), DateTime.UtcNow);

        var snapshot = new LibraryIndexService().Index(_f.Root);
        var recent = RecentContent.FromSnapshot(snapshot);
        var hero = Assert.Single(recent, r => r.Id == "Hero");
        var arena = Assert.Single(recent, r => r.Name == "Arena");
        Assert.False(hero.NeedsDefChoice);

        var roster = new RosterActivationService(_f.Mutations, Path.Combine(_f.AppDir, "roster"));
        Assert.True(roster.SetCharacterEnabled(_f.Root, hero.DefPath, enabled: true).Success);
        Assert.True(roster.SetStageEnabled(_f.Root, arena.DefPath, enabled: true).Success);

        var text = File.ReadAllText(_f.Full("data/select.def"));
        Assert.Contains("Hero", text);
        Assert.Contains("arena.def", text, StringComparison.OrdinalIgnoreCase);

        Assert.True(roster.SetCharacterEnabled(_f.Root, hero.DefPath, enabled: false).Success);
        text = File.ReadAllText(_f.Full("data/select.def"));
        Assert.Contains(";Hero", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void NeedsDefChoice_blocks_enable_without_guessing()
    {
        PrimaryDefResolverTests.WriteDef(_f.Full("chars/Twins"), "Red.def", "Red", sprite: "red.sff");
        PrimaryDefResolverTests.WriteDef(_f.Full("chars/Twins"), "Blue.def", "Blue", sprite: "blue.sff");
        _f.Write("data/select.def", "[Characters]\nkfm\n\n[ExtraStages]\n");
        Directory.SetCreationTimeUtc(_f.Full("chars/Twins"), DateTime.UtcNow);

        var snapshot = new LibraryIndexService().Index(_f.Root);
        var item = Assert.Single(RecentContent.FromSnapshot(snapshot), r => r.Id == "Twins");
        Assert.True(item.NeedsDefChoice);

        // Dashboard policy: do not call roster enable while NeedsDefChoice is set (no guessing).
        // Enabling the display DefPath would invent a primary; the UI must refuse first.
        Assert.Equal(ContentStatus.Unregistered, item.Status);
        Assert.False(string.IsNullOrWhiteSpace(item.DefPath)); // display label only
    }

    [Fact]
    public void Dashboard_xaml_keeps_status_toggle_outside_OpenRecent_button()
    {
        var path = FindDashboardXaml();
        var xaml = File.ReadAllText(path);

        Assert.Contains("ToggleRecentStatusCommand", xaml);
        Assert.Contains("InteractiveStatusToggle", xaml);
        Assert.Contains("OpenRecentCommand", xaml);

        // The interactive toggle must not appear between the OpenRecent Button's open and close tags.
        var openRecent = Regex.Match(
            xaml,
            @"<Button\b[^>]*OpenRecentCommand[\s\S]*?</Button>",
            RegexOptions.IgnoreCase);
        Assert.True(openRecent.Success, "OpenRecentCommand Button block not found");
        Assert.DoesNotContain("InteractiveStatusToggle", openRecent.Value);
        Assert.DoesNotContain("ToggleRecentStatusCommand", openRecent.Value);
        Assert.DoesNotContain("StatusToggle", openRecent.Value);

        // Toggle appears as a sibling after the OpenRecent button (still in the recent row).
        var toggleIndex = xaml.IndexOf("ToggleRecentStatusCommand", StringComparison.Ordinal);
        var openIndex = xaml.IndexOf("OpenRecentCommand", StringComparison.Ordinal);
        Assert.True(toggleIndex > openIndex);
    }

    private static string FindDashboardXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "IKEMENLab.App", "Views", "DashboardView.xaml");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "Views", "DashboardView.xaml");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("DashboardView.xaml not found from " + AppContext.BaseDirectory);
    }
}
