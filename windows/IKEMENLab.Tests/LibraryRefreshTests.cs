using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Refresh re-runs the one library index (the same pipeline as startup) against what is on disk now, so
/// changes made outside IKEMEN Lab — folders copied in or deleted, select.def edited, config.ini switched
/// to another motif — show up without restarting.
/// </summary>
public sealed class LibraryRefreshTests : IDisposable
{
    private readonly MutationFixture _f = MutationFixture.SameVolume();
    private DateTime _now = DateTime.UtcNow.AddMinutes(1);
    private readonly LibraryIndexService _index;

    public LibraryRefreshTests()
    {
        var tracker = new DateAddedTracker(new DateAddedStore(Path.Combine(_f.AppDir, "date-added")), () => _now);
        _index = new LibraryIndexService(tracker, new PrimaryDefStore(Path.Combine(_f.AppDir, "primary-def")));
    }

    public void Dispose() => _f.Dispose();

    private void Char(string folder) => PrimaryDefResolverTests.WriteDef(_f.Full("chars/" + folder), folder + ".def", folder);

    private static CharacterEntry C(LibrarySnapshot s, string id) => s.Characters.Single(c => c.Id == id);

    [Fact]
    public void RefreshReflectsManualFolderSelectDefAndConfigChanges()
    {
        Char("Naruto");
        Char("Gaara");
        Char("Muzan");
        _f.Write("stages/leaf.def", "[Info]\nname = Leaf\n[BGdef]\nspr = leaf.sff\n");
        _f.Write("save/config.ini", "[Config]\nMotif = data/system.def\n\n[Video]\nVSync = 1\n");
        _f.Write("data/system.def", "[Files]\nselect = select.def\n");
        _f.Write("data/select.def", "[Characters]\nNaruto\nGaara\n;Muzan\n\n[ExtraStages]\nstages/leaf.def\n");

        var first = _index.Index(_f.Root);
        Assert.Equal(3, first.CharacterCount);
        Assert.Equal(ContentStatus.Disabled, C(first, "Muzan").Status);
        Assert.Equal(true, first.Config.VSync);
        Assert.Equal(DateAddedSource.LegacyEstimate, C(first, "Gaara").DateAddedSource);

        // Outside IKEMEN Lab: Gaara's folder deleted by hand, CHASE copied in, a stage added, select.def
        // re-ordered in a text editor, and config.ini pointed at another motif with its own select.def.
        _now = _now.AddMinutes(5);
        Directory.Delete(_f.Full("chars/Gaara"), recursive: true);
        Char("CHASE");
        _f.Write("stages/sand.def", "[Info]\nname = Sand\n[BGdef]\nspr = sand.sff\n");
        _f.Write("data/select.def", "[Characters]\nMuzan\nNaruto\n\n[ExtraStages]\nstages/leaf.def\n");
        _f.Write("data/alt/system.def", "[Files]\nselect = alt-select.def\n");
        _f.Write("data/alt/alt-select.def", "[Characters]\nCHASE\nMuzan\n;Naruto\n\n[ExtraStages]\nstages/sand.def\n");
        _f.Write("save/config.ini", "[Config]\nMotif = data/alt/system.def\n\n[Video]\nVSync = 0\n");

        var refreshed = _index.Index(_f.Root);

        Assert.Equal(["CHASE", "Muzan", "Naruto"], refreshed.Characters.Select(c => c.Id).Order());
        Assert.Equal(2, refreshed.StageCount);
        Assert.Equal(false, refreshed.Config.VSync);
        Assert.Equal(Path.GetFullPath(_f.Full("data/alt/alt-select.def")), refreshed.SelectDef!.Location.Path);
        Assert.Equal(ContentStatus.Active, C(refreshed, "CHASE").Status);
        Assert.Equal(ContentStatus.Active, C(refreshed, "Muzan").Status);
        Assert.Equal(ContentStatus.Disabled, C(refreshed, "Naruto").Status);
        Assert.Equal(ContentStatus.Active, refreshed.Stages.Single(s => s.DefPath == "sand.def").Status);
        Assert.Equal(ContentStatus.Unregistered, refreshed.Stages.Single(s => s.DefPath == "leaf.def").Status);
        Assert.Equal(2, refreshed.ActiveCharacterCount);

        // Date Added: the new folder is dated as a new arrival; existing ones keep their records.
        Assert.Equal(DateAddedSource.FirstSeen, C(refreshed, "CHASE").DateAddedSource);
        Assert.Equal(C(first, "Naruto").DateAddedUtc, C(refreshed, "Naruto").DateAddedUtc);

        // Dashboard "Recently Installed" is derived from the same snapshot.
        var recent = RecentContent.FromSnapshot(refreshed);
        Assert.Equal("CHASE", recent.First(r => r.Type == RecentContentType.Character).Id);
        Assert.DoesNotContain(recent, r => r.Id == "Gaara");
    }

    [Fact]
    public void RefreshPicksUpARosterEditMadeWhileTheAppWasOpen()
    {
        Char("Naruto");
        Char("Gaara");
        _f.Write("data/select.def", "[Characters]\nNaruto\n");
        Assert.Equal(ContentStatus.Unregistered, C(_index.Index(_f.Root), "Gaara").Status);

        _f.Write("data/select.def", "[Characters]\nNaruto\nGaara\n");

        Assert.Equal(ContentStatus.Active, C(_index.Index(_f.Root), "Gaara").Status);
    }

    private sealed record Row(string Id);

    private static readonly Row[] Rows = [new("Naruto"), new("Gaara"), new("Muzan"), new("CHASE")];

    [Fact]
    public void SelectionIsKeptByIdAcrossARebuild()
    {
        var rebuilt = Rows.Select(r => new Row(r.Id)).ToList();
        var kept = BrowserSelection.AfterRefresh(rebuilt, r => r.Id, "muzan", selectFirstWhenNone: true);
        Assert.Same(rebuilt[2], kept);
    }

    [Fact]
    public void SelectionIsClearedWhenTheItemIsGone()
        => Assert.Null(BrowserSelection.AfterRefresh(Rows, r => r.Id, "Gaara2", selectFirstWhenNone: true));

    [Fact]
    public void FirstLoadSelectsTheFirstItemOnlyWhenAsked()
    {
        Assert.Same(Rows[0], BrowserSelection.AfterRefresh(Rows, r => r.Id, null, selectFirstWhenNone: true));
        Assert.Null(BrowserSelection.AfterRefresh(Rows, r => r.Id, null, selectFirstWhenNone: false));
    }

    [Theory]
    [InlineData("Gaara", "Muzan")]
    [InlineData("Naruto", "Gaara")]
    [InlineData("CHASE", "Muzan")]
    [InlineData("Nobody", null)]
    public void AdjacentSelectionAfterRemovalPrefersTheNextItem(string removed, string? expected)
        => Assert.Equal(expected, BrowserSelection.AdjacentId(Rows, r => r.Id, removed));

    [Fact]
    public void RemovingTheOnlyItemLeavesNothingSelected()
        => Assert.Null(BrowserSelection.AdjacentId([new Row("Solo")], r => r.Id, "Solo"));
}
