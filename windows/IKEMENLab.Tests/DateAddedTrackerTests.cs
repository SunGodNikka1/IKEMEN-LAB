using IKEMENLab.Core.Install;
using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using IKEMENLab.Core.Sprites;
using Xunit;

namespace IKEMENLab.Tests;

public class DateAddedTrackerTests
{
    /// <summary>Clock anchored to real time so real file timestamps and tracker time are comparable.</summary>
    private sealed class TestClock
    {
        public DateTime Now { get; set; } = DateTime.UtcNow.AddMinutes(1);
        public DateTime Advance(TimeSpan by) => Now = Now + by;
    }

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Fixture = MutationFixture.SameVolume();
            StoreDir = Path.Combine(Fixture.AppDir, "date-added");
            Tracker = NewTracker();
        }

        public MutationFixture Fixture { get; }
        public string Root => Fixture.Root;
        public string StoreDir { get; }
        public TestClock Clock { get; } = new();
        public DateAddedTracker Tracker { get; private set; }

        public DateAddedTracker NewTracker() => new(new DateAddedStore(StoreDir), () => Clock.Now);

        public void Restart() => Tracker = NewTracker();

        public LibrarySnapshot Index() => new LibraryIndexService(Tracker).Index(Root);

        public CharacterEntry Char(string id) => Index().Characters.Single(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        public StageEntry Stage(string id) => Index().Stages.Single(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        public ContentInstallService Installer() => new(Fixture.Mutations, Tracker);

        public InstallBatchResult Install(string sourceDir, InstallItemDecision? decision = null)
        {
            var installer = Installer();
            var inspect = installer.Inspect([sourceDir], Root, Path.Combine(Fixture.AppDir, "install-staging"));
            foreach (var item in inspect.Items)
            {
                item.Decision = decision ?? (item.DestinationExists ? InstallItemDecision.Replace : InstallItemDecision.InstallNew);
            }

            var result = installer.Execute(inspect.Items, Root);
            installer.CleanupStaging(inspect.StagingDirectories);
            return result;
        }

        public string MakeCharPackage(string folder, string? extra = null)
        {
            var dir = Path.Combine(Fixture.AppDir, "incoming-" + Guid.NewGuid().ToString("N"), folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, folder + ".def"),
                $"[Info]\nname = {folder}\nauthor = Test{extra}\n[Files]\ncmd = x.cmd\ncns = x.cns\nsprite = x.sff\nanim = x.air\n");
            foreach (var f in new[] { "x.cmd", "x.cns", "x.sff", "x.air" }) File.WriteAllText(Path.Combine(dir, f), "x");
            return Path.GetDirectoryName(dir)!;
        }

        public string MakeStagePackage(string folder)
        {
            var dir = Path.Combine(Fixture.AppDir, "incoming-" + Guid.NewGuid().ToString("N"), folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, folder + ".def"), $"[Info]\nname = {folder}\n[BGdef]\nspr = {folder}.sff\n[Camera]\nboundleft = -100\n");
            File.WriteAllText(Path.Combine(dir, folder + ".sff"), "x");
            return Path.GetDirectoryName(dir)!;
        }

        public void Dispose() => Fixture.Dispose();
    }

    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    [Fact]
    public void AppInstalledCharacterAndStageGetExactDate()
    {
        using var h = new Harness();
        h.Fixture.WriteChar("kfm");
        Assert.Equal(DateAddedSource.LegacyEstimate, h.Char("kfm").DateAddedSource);

        var installAt = h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(InstallItemOutcome.Installed, h.Install(h.MakeCharPackage("berdly")).Items.Single().Outcome);
        Assert.Equal(InstallItemOutcome.Installed, h.Install(h.MakeStagePackage("arena")).Items.Single().Outcome);

        h.Clock.Advance(TimeSpan.FromHours(1));
        var berdly = h.Char("berdly");
        Assert.Equal(DateAddedSource.Installed, berdly.DateAddedSource);
        Assert.Equal(installAt, berdly.DateAddedUtc);
        var arena = h.Stage("arena/arena.def");
        Assert.Equal(DateAddedSource.Installed, arena.DateAddedSource);
        Assert.Equal(installAt, arena.DateAddedUtc);
    }

    [Fact]
    public void LaterInstallSortsNewerAndReplacementKeepsOriginalDate()
    {
        using var h = new Harness();
        h.Index();
        var first = h.Clock.Advance(TimeSpan.FromMinutes(10));
        h.Install(h.MakeCharPackage("alpha"));
        var second = h.Clock.Advance(TimeSpan.FromMinutes(10));
        h.Install(h.MakeCharPackage("bravo"));

        // Update alpha much later: it must not become the newest.
        h.Clock.Advance(TimeSpan.FromHours(5));
        var replaced = h.Install(h.MakeCharPackage("alpha", extra: " v2"), InstallItemDecision.Replace);
        Assert.Equal(InstallItemOutcome.Installed, replaced.Items.Single().Outcome);
        Assert.Contains("v2", File.ReadAllText(h.Fixture.Full("chars/alpha/alpha.def")));

        var snapshot = h.Index();
        Assert.Equal(first, snapshot.Characters.Single(c => c.Id == "alpha").DateAddedUtc);
        Assert.Equal(second, snapshot.Characters.Single(c => c.Id == "bravo").DateAddedUtc);
        var latest = DateAddedSort.Apply(snapshot.Characters, BrowserSortMode.LatestAdded, c => c.DateAddedUtc, c => c.DisplayName);
        Assert.Equal(["bravo", "alpha"], latest.Select(c => c.Id));
    }

    [Fact]
    public void ReplacingLegacyContentKeepsItsEstimatedDate()
    {
        using var h = new Harness();
        h.Fixture.WriteChar("old");
        Directory.SetCreationTimeUtc(h.Fixture.Full("chars/old"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        h.Clock.Advance(TimeSpan.FromHours(1));
        h.Install(h.MakeCharPackage("old"), InstallItemDecision.Replace); // before any index/baseline

        var old = h.Char("old");
        Assert.Equal(DateAddedSource.LegacyEstimate, old.DateAddedSource);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), old.DateAddedUtc);
    }

    [Fact]
    public void FailedRejectedSkippedAndDryRunInstallsCreateNoRecord()
    {
        using var h = new Harness();
        h.Fixture.WriteChar("dup");
        var before = h.Char("dup").DateAddedUtc;
        var store = new DateAddedStore(h.StoreDir);

        h.Clock.Advance(TimeSpan.FromHours(1));
        h.Install(h.MakeCharPackage("dup"), InstallItemDecision.Skip);
        h.Install(h.MakeCharPackage("dup"), InstallItemDecision.NeedsDecision); // rejected
        var installer = h.Installer();
        var inspect = installer.Inspect([h.MakeCharPackage("ghost")], h.Root, Path.Combine(h.Fixture.AppDir, "st"));
        installer.Execute(inspect.Items, h.Root, dryRun: true);

        Assert.Equal(before, h.Char("dup").DateAddedUtc);
        Assert.Equal(DateAddedSource.LegacyEstimate, h.Char("dup").DateAddedSource);
        Assert.DoesNotContain("character:ghost", store.Read(h.Root).Items.Keys);
        Assert.False(Directory.Exists(h.Fixture.Full("chars/ghost")));
    }

    [Fact]
    public void DatesSurviveRestartReindexEditsTogglesAndThumbnails()
    {
        using var h = new Harness();
        h.Fixture.WriteChar("kfm");
        h.Fixture.Write("data/select.def", "[Characters]\nkfm\n");
        h.Index();
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Install(h.MakeCharPackage("berdly"));
        var original = h.Char("berdly");
        var kfmOriginal = h.Char("kfm").DateAddedUtc;

        // Edit the DEF, add a file (folder change time moves) and re-index repeatedly.
        h.Clock.Advance(TimeSpan.FromDays(3));
        File.AppendAllText(h.Fixture.Full("chars/berdly/berdly.def"), "\n; edited\n");
        File.WriteAllText(h.Fixture.Full("chars/berdly/extra.act"), "x");
        for (var i = 0; i < 3; i++) h.Index();

        // Roster toggles and thumbnail generation.
        var roster = new RosterActivationService(h.Fixture.Mutations, h.Fixture.StagingDir);
        Assert.True(roster.SetCharacterEnabled(h.Root, "chars/berdly/berdly.def", true).Success);
        Assert.True(roster.SetCharacterEnabled(h.Root, "chars/berdly/berdly.def", false).Success);
        var artwork = new ArtworkService(h.Root, new ThumbnailCache(Path.Combine(h.Fixture.AppDir, "thumbs")));
        artwork.CharacterPortraitPng(h.Char("berdly"));

        h.Restart();
        var after = h.Char("berdly");
        Assert.Equal(original.DateAddedUtc, after.DateAddedUtc);
        Assert.Equal(DateAddedSource.Installed, after.DateAddedSource);
        Assert.Equal(kfmOriginal, h.Char("kfm").DateAddedUtc);
    }

    [Fact]
    public void MovedInContentIsNotOutrankedByAnEarlierCopy()
    {
        // The reported defect: Muzan was copied in (fresh CreationTime) before other characters were
        // moved in from an extracted pack on the same drive, which keeps their older CreationTime.
        using var h = new Harness();
        var now = DateTime.UtcNow;

        h.Fixture.WriteChar("Muzan");
        Directory.SetCreationTimeUtc(h.Fixture.Full("chars/Muzan"), now.AddDays(-2));

        var extracted = Path.Combine(h.Fixture.AppDir, "Downloads", "Afro Samurai");
        Directory.CreateDirectory(extracted);
        File.WriteAllText(Path.Combine(extracted, "Afro Samurai.def"), TestRoot.CharDef("Afro Samurai"));
        Directory.SetCreationTimeUtc(extracted, now.AddDays(-3));
        Directory.SetLastWriteTimeUtc(extracted, now.AddDays(-3));
        Directory.Move(extracted, h.Fixture.Full("chars/Afro Samurai"));

        Assert.True(Directory.GetCreationTimeUtc(h.Fixture.Full("chars/Afro Samurai")) <
                    Directory.GetCreationTimeUtc(h.Fixture.Full("chars/Muzan")));

        var snapshot = h.Index();
        var latest = DateAddedSort.Apply(snapshot.Characters, BrowserSortMode.LatestAdded, c => c.DateAddedUtc, c => c.DisplayName)
            .Select(c => c.Id).ToList();
        Assert.Equal(["Afro Samurai", "Muzan"], latest);
        Assert.All(snapshot.Characters, c => Assert.Equal(DateAddedSource.LegacyEstimate, c.DateAddedSource));
    }

    [Fact]
    public void ManualContentAfterBaselineIsFirstSeenAndStable()
    {
        using var h = new Harness();
        h.Fixture.WriteChar("kfm");
        h.Index();
        var windowStart = h.Clock.Advance(TimeSpan.FromHours(1));
        h.Index();

        h.Fixture.WriteChar("manual");
        h.Clock.Advance(TimeSpan.FromHours(1));
        var first = h.Char("manual");
        Assert.Equal(DateAddedSource.FirstSeen, first.DateAddedSource);
        Assert.InRange(first.DateAddedUtc!.Value, windowStart, h.Clock.Now);

        h.Clock.Advance(TimeSpan.FromDays(2));
        h.Restart();
        Assert.Equal(first.DateAddedUtc, h.Char("manual").DateAddedUtc);
    }

    [Fact]
    public void DeletedThenReAddedContentIsNewButUnindexableContentKeepsItsDate()
    {
        using var h = new Harness();
        h.Fixture.WriteChar("gone");
        h.Fixture.WriteChar("editing");
        h.Index();
        var editingDate = h.Char("editing").DateAddedUtc;

        // "editing": DEF temporarily invalid (not indexable) but folder present.
        var def = h.Fixture.Full("chars/editing/editing.def");
        var saved = File.ReadAllText(def);
        File.WriteAllText(def, "garbage");
        Directory.Delete(h.Fixture.Full("chars/gone"), recursive: true);
        var removedAt = h.Clock.Advance(TimeSpan.FromHours(1));
        var mid = h.Index();
        Assert.DoesNotContain(mid.Characters, c => c.Id is "gone" or "editing");

        File.WriteAllText(def, saved);
        h.Fixture.WriteChar("gone");
        h.Clock.Advance(TimeSpan.FromHours(1));
        var snapshot = h.Index();

        Assert.Equal(editingDate, snapshot.Characters.Single(c => c.Id == "editing").DateAddedUtc);
        var gone = snapshot.Characters.Single(c => c.Id == "gone");
        Assert.Equal(DateAddedSource.FirstSeen, gone.DateAddedSource);
        Assert.True(gone.DateAddedUtc >= removedAt);
    }

    [Fact]
    public void NestedIdentityAndCaseAndSlashNormalisation()
    {
        using var h = new Harness();
        h.Fixture.WriteChar("Pack/Ken");
        var ken = h.Char("Pack/Ken");
        Assert.Equal("character:pack", ContentIdentity.ForCharacter(ken));
        Assert.Equal("character:pack", ContentIdentity.ForCharacterFolder("PACK"));
        Assert.Equal("stage:pack/a.def", ContentIdentity.ForStageDef(@"stages\Pack\A.def"));

        // Same display name in two folders stays two identities.
        h.Fixture.Write("chars/ryu1/ryu1.def", TestRoot.CharDef("Ryu"));
        h.Fixture.Write("chars/ryu2/ryu2.def", TestRoot.CharDef("Ryu"));
        var ryus = h.Index().Characters.Where(c => c.DisplayName == "Ryu").Select(ContentIdentity.ForCharacter).ToList();
        Assert.Equal(2, ryus.Distinct().Count());
    }

    [Fact]
    public void SeparateInstallationsDoNotShareRecords()
    {
        using var a = new Harness();
        using var b = MutationFixture.SameVolume();
        a.Fixture.WriteChar("kfm");
        b.WriteChar("kfm");
        Directory.SetCreationTimeUtc(b.Full("chars/kfm"), new DateTime(2019, 5, 5, 0, 0, 0, DateTimeKind.Utc));

        var store = new DateAddedStore(a.StoreDir);
        Assert.NotEqual(store.PathFor(a.Root), store.PathFor(b.Root));
        var da = a.Char("kfm").DateAddedUtc;
        var db = new LibraryIndexService(a.Tracker).Index(b.Root).Characters.Single().DateAddedUtc;
        Assert.Equal(new DateTime(2019, 5, 5, 0, 0, 0, DateTimeKind.Utc), db);
        Assert.NotEqual(da, db);
        Assert.Equal(da, a.Char("kfm").DateAddedUtc);
    }

    [Fact]
    public void RecordsLiveOutsideTheIkemenRoot()
    {
        using var h = new Harness();
        h.Fixture.WriteChar("kfm");
        var before = Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();
        h.Index();
        h.Index();
        Assert.Equal(before, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList());
        Assert.StartsWith(h.StoreDir, new DateAddedStore(h.StoreDir).PathFor(h.Root), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DashboardRecentUsesTheSameChronologyAsTheBrowsers()
    {
        using var h = new Harness();
        h.Fixture.WriteChar("kfm");
        h.Index();
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Install(h.MakeStagePackage("arena"));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Install(h.MakeCharPackage("berdly"));

        var snapshot = h.Index();
        var recent = RecentContent.FromSnapshot(snapshot);
        Assert.Equal(["berdly", "arena/arena.def", "kfm"], recent.Select(r => r.Id));
        Assert.Equal(snapshot.Characters.Single(c => c.Id == "berdly").DateAddedUtc, recent[0].DateAddedUtc);
        Assert.Equal(DateAddedSource.Installed, recent[0].DateAddedSource);
        Assert.True(recent[2].IsEstimated);
    }

    [Fact]
    public void LegacyValuesAreLabelledAsEstimates()
    {
        var at = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.StartsWith("Estimated added ", DateAddedSort.FormatAddedLabel(at, DateAddedSource.LegacyEstimate));
        Assert.StartsWith("Added ", DateAddedSort.FormatAddedLabel(at, DateAddedSource.Installed));
        Assert.StartsWith("Added ", DateAddedSort.FormatAddedLabel(at, DateAddedSource.FirstSeen));
        Assert.Null(DateAddedSort.FormatAddedLabel(null, DateAddedSource.Installed));
    }

    [Fact]
    public void StageLatestAndOldestUseTrackedDates()
    {
        using var h = new Harness();
        h.Index();
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Install(h.MakeStagePackage("zeta"));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Install(h.MakeStagePackage("alpha"));

        var stages = h.Index().Stages;
        Assert.Equal(["alpha/alpha.def", "zeta/zeta.def"],
            DateAddedSort.Apply(stages, BrowserSortMode.LatestAdded, s => s.DateAddedUtc, s => s.Name, s => s.DefPath).Select(s => s.Id));
        Assert.Equal(["zeta/zeta.def", "alpha/alpha.def"],
            DateAddedSort.Apply(stages, BrowserSortMode.OldestAdded, s => s.DateAddedUtc, s => s.Name, s => s.DefPath).Select(s => s.Id));
    }

    [Fact]
    public void WithoutAStoreEntriesGetUnsavedEstimates()
    {
        using var root = new TestRoot();
        root.Write("chars/kfm/kfm.def", TestRoot.CharDef("kfm"));
        var c = new LibraryIndexService().Index(root.Path).Characters.Single();
        Assert.Equal(DateAddedSource.LegacyEstimate, c.DateAddedSource);
        Assert.NotNull(c.DateAddedUtc);
    }
}
