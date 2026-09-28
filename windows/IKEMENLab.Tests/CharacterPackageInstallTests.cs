using System.IO.Compression;
using IKEMENLab.Core.Install;
using IKEMENLab.Core.Library;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Character package identity: the destination folder follows extraction structure (never a DEF file
/// name), the primary DEF is decided separately, ambiguity is asked, and collisions offer
/// Replace / Skip / Rename.
/// </summary>
public sealed class CharacterPackageInstallTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ikemenlab-pkgid-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly SafeMutationService _mutations;
    private readonly PrimaryDefStore _store;
    private readonly ContentInstallService _installer;
    private readonly string _staging;

    public CharacterPackageInstallTests()
    {
        _root = Path.Combine(_work, "ikemen");
        foreach (var d in new[] { "chars", "stages", "data", "save" }) Directory.CreateDirectory(Path.Combine(_root, d));
        File.WriteAllText(Path.Combine(_root, "data", "select.def"), "[Characters]\nkfm\n\n[ExtraStages]\n");
        _staging = Path.Combine(_work, "staging");
        _mutations = new SafeMutationService(Path.Combine(_work, "ops"), Path.Combine(_work, "bak"));
        _store = new PrimaryDefStore(Path.Combine(_work, "app", "primary-def"));
        _installer = new ContentInstallService(_mutations, primaryDefs: _store);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_work, true);
        }
        catch (IOException) { }
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private string Src(string name)
    {
        var dir = Path.Combine(_work, "src-" + Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Zips the contents of <paramref name="contents"/> (not the folder itself) as <paramref name="archiveName"/>.</summary>
    private string Zip(string contents, string archiveName)
    {
        var dir = Path.Combine(_work, "zips-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, archiveName);
        ZipFile.CreateFromDirectory(contents, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        return zip;
    }

    private static void Def(string folder, string file, string name, string sprite = "char.sff")
        => PrimaryDefResolverTests.WriteDef(folder, file, name, sprite);

    private static void Muzan(string folder)
    {
        Def(folder, "Muzan.def", "Demon King Muzan");
        Def(folder, "Muzan_AI.def", "Demon King Muzan");
        Def(folder, "Muzan_noAI.def", "Demon King Muzan");
        PrimaryDefResolverTests.WriteKfmPlaceholder(folder);
    }

    private InspectBatchResult Inspect(params string[] inputs) => _installer.Inspect(inputs, _root, _staging);

    private string Chars(string relative) => Path.Combine(_root, "chars", relative.Replace('/', Path.DirectorySeparatorChar));

    // ---- folder identity ------------------------------------------------------------------------

    [Fact]
    public void ArchiveCharacterFolderKeepsItsNameAndAKfmPlaceholderCannotRenameIt()
    {
        Def(Chars("kfm"), "kfm.def", "Kung Fu Man", sprite: "kfm.sff");
        File.WriteAllText(Chars("kfm/marker.txt"), "real kfm");
        var src = Src("payload");
        Muzan(Path.Combine(src, "Muzan"));

        var inspect = Inspect(Zip(src, "Muzan.zip"));

        var item = Assert.Single(inspect.Items);
        Assert.Equal("Muzan", item.DestinationName);
        Assert.Equal(FolderNameSource.CharacterFolder, item.Package.NameSource);
        Assert.False(item.DestinationExists);
        Assert.Equal("Muzan.def", item.Package.PrimaryDef!.RelativePath);
        Assert.Equal(PrimaryDefRule.FolderName, item.Package.PrimaryRule);
        Assert.Equal(4, item.Package.DefCandidates.Count);
        Assert.True(item.Package.DefCandidates.Single(c => c.FileName == "KFM.def").IsPlaceholder);

        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);
        Assert.True(File.Exists(Chars("Muzan/Muzan_noAI.def")));
        Assert.True(File.Exists(Chars("Muzan/KFM.def")));
        Assert.Equal("real kfm", File.ReadAllText(Chars("kfm/marker.txt")));
        Assert.False(Directory.Exists(Chars("KFM_")));
    }

    [Fact]
    public void FlatArchiveIsNamedAfterTheArchiveAndItsDefIsListedByPath()
    {
        var src = Src("flat");
        Def(src, "char.def", "Muzan");

        var inspect = Inspect(Zip(src, "Muzan V3.zip"));

        var item = Assert.Single(inspect.Items);
        Assert.Equal("Muzan V3", item.DestinationName);
        Assert.Equal(FolderNameSource.ArchiveName, item.Package.NameSource);
        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);
        Assert.True(File.Exists(Chars("Muzan V3/char.def")));
        Assert.False(Directory.Exists(Chars("char")));

        var entry = new LibraryIndexService().Index(_root).Characters.Single(c => c.Id == "Muzan V3");
        Assert.Equal("chars/Muzan V3/char.def", entry.DefPath);
        Assert.True(new RosterActivationService(_mutations, Path.Combine(_work, "roster")).SetCharacterEnabled(_root, entry.DefPath, true).Success);
        Assert.Contains("Muzan V3/char.def", File.ReadAllText(Path.Combine(_root, "data", "select.def")));
    }

    [Fact]
    public void FlatArchiveWithAiAlternativesPicksTheBaseDef()
    {
        var src = Src("flat");
        Def(src, "Akaza.def", "Akaza");
        Def(src, "Akaza_AI.def", "Akaza");

        var item = Assert.Single(Inspect(Zip(src, "Akaza Pack.zip")).Items);

        Assert.Equal("Akaza Pack", item.DestinationName);
        Assert.Equal(PrimaryDefRule.AiVariantBase, item.Package.PrimaryRule);
        Assert.Equal("Akaza.def", item.Package.PrimaryDef!.RelativePath);
    }

    [Fact]
    public void WrapperFoldersAreDroppedByStructureAndTheirDocsLeftOut()
    {
        var src = Src("payload");
        var wrapper = Path.Combine(src, "Pack v1");
        Def(Path.Combine(wrapper, "Akaza"), "Akaza.def", "Akaza");
        File.WriteAllText(Path.Combine(wrapper, "readme.txt"), "hi");
        Directory.CreateDirectory(Path.Combine(wrapper, "Screens"));
        File.WriteAllText(Path.Combine(wrapper, "Screens", "shot.png"), "png");

        var inspect = Inspect(Zip(src, "Pack.zip"));

        var item = Assert.Single(inspect.Items);
        Assert.Equal("Akaza", item.DestinationName);
        Assert.Equal(["Pack v1"], item.Package.Wrappers);
        Assert.Contains("Pack v1/readme.txt", item.Package.LeftOutDocs);
        Assert.Contains("Pack v1/Screens/", item.Package.LeftOutDocs);
        Assert.Empty(item.Package.LeftOutFiles);
        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);
        Assert.False(File.Exists(Chars("Akaza/readme.txt")));
    }

    [Fact]
    public void FolderNamesThatLookGenericAreKeptExactly()
    {
        var src = Src("payload");
        Def(Path.Combine(src, "release"), "char.def", "Someone");
        Assert.Equal("release", Assert.Single(Inspect(Zip(src, "anything.zip")).Items).DestinationName);

        var dropped = Src("char");
        Def(dropped, "char.def", "Someone");
        Assert.Equal("char", Assert.Single(Inspect(dropped).Items).DestinationName);
    }

    [Fact]
    public void DroppedFolderKeepsItsExactNameOrIsTreatedAsAWrapper()
    {
        var dropped = Src("Kaigaku Final");
        Def(dropped, "Kaigaku.def", "Kaigaku");
        var item = Assert.Single(Inspect(dropped).Items);
        Assert.Equal("Kaigaku Final", item.DestinationName);
        Assert.Equal(FolderNameSource.DroppedFolder, item.Package.NameSource);

        var wrapper = Src("Downloads Stuff");
        Def(Path.Combine(wrapper, "Kaigaku"), "Kaigaku.def", "Kaigaku");
        var wrapped = Assert.Single(Inspect(wrapper).Items);
        Assert.Equal("Kaigaku", wrapped.DestinationName);
        Assert.Equal(["Downloads Stuff"], wrapped.Package.Wrappers);
    }

    [Fact]
    public void SeveralCharacterFoldersBecomeSeparatePackages()
    {
        var src = Src("payload");
        Def(Path.Combine(src, "Duo", "Ryu"), "Ryu.def", "Ryu");
        Def(Path.Combine(src, "Duo", "Ken"), "Ken.def", "Ken");

        var inspect = Inspect(Zip(src, "Duo.zip"));

        Assert.Equal(["Ken", "Ryu"], inspect.Items.Select(i => i.DestinationName).OrderBy(n => n));
        Assert.Equal(2, _installer.Execute(inspect.Items, _root).InstalledCount);
    }

    [Fact]
    public void DefFoldersInsideACharacterStayPartOfIt()
    {
        var src = Src("payload");
        Def(Path.Combine(src, "Muzan"), "Muzan.def", "Muzan");
        Def(Path.Combine(src, "Muzan", "AI patch"), "Muzan.def", "Muzan");

        var inspect = Inspect(Zip(src, "Muzan.zip"));

        var item = Assert.Single(inspect.Items);
        Assert.Equal("Muzan", item.DestinationName);
        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);
        Assert.True(File.Exists(Chars("Muzan/AI patch/Muzan.def")));
    }

    [Fact]
    public void FilesReferencedOutsideTheDefFolderGrowThePackage()
    {
        var src = Src("payload");
        var shared = Path.Combine(src, "Shared");
        Def(Path.Combine(shared, "Hero"), "Hero.def", "Hero", sprite: "../common/hero.sff");

        var inspect = Inspect(Zip(src, "Shared.zip"));

        var item = Assert.Single(inspect.Items);
        Assert.Equal("Shared", item.DestinationName);
        Assert.Equal("Hero/Hero.def", item.Package.PrimaryDef!.RelativePath);
        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);
        Assert.True(File.Exists(Chars("Shared/common/hero.sff")));
    }

    [Fact]
    public void LooseRuntimeFilesOutsideTheCharacterNeedConfirmation()
    {
        var src = Src("payload");
        Def(Path.Combine(src, "Loose", "Gyutaro"), "Gyutaro.def", "Gyutaro");
        File.WriteAllText(Path.Combine(src, "Loose", "extra.sff"), "?");

        var inspect = Inspect(Zip(src, "Loose.zip"));
        var item = Assert.Single(inspect.Items);
        Assert.Equal("Gyutaro", item.DestinationName);
        Assert.Equal(["Loose/extra.sff"], item.Package.LeftOutFiles);

        var blocked = _installer.Execute(inspect.Items, _root);
        Assert.Equal(InstallItemOutcome.Rejected, blocked.Items[0].Outcome);
        Assert.False(Directory.Exists(Chars("Gyutaro")));

        item.LayoutConfirmed = true;
        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);
        Assert.False(File.Exists(Chars("Gyutaro/extra.sff")));
    }

    // ---- primary DEF ------------------------------------------------------------------------------

    [Fact]
    public void AmbiguousDefsMustBeChosenAndTheChoiceIsSavedOutsideTheRoot()
    {
        var src = Src("payload");
        Def(Path.Combine(src, "Twins"), "Red.def", "Red", sprite: "red.sff");
        Def(Path.Combine(src, "Twins"), "Blue.def", "Blue", sprite: "blue.sff");

        var inspect = Inspect(Zip(src, "Twins.zip"));
        var item = Assert.Single(inspect.Items);
        Assert.True(item.NeedsDefChoice);

        var blocked = _installer.Execute(inspect.Items, _root);
        Assert.Equal(InstallItemOutcome.Rejected, blocked.Items[0].Outcome);
        Assert.Contains("Choose which one", blocked.Items[0].Error);
        Assert.False(Directory.Exists(Chars("Twins")));

        item.SelectedDef = "Red.def";
        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);
        Assert.Equal("Red.def", _store.Get(_root, "Twins"));
        Assert.False(Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories).Any());

        var entry = new LibraryIndexService(primaryDefs: _store).Index(_root).Characters.Single(c => c.Id == "Twins");
        Assert.Equal("chars/Twins/Red.def", entry.DefPath);
        Assert.False(entry.NeedsDefChoice);
    }

    [Fact]
    public void ChoosingAnAlternateDefDoesNotRenameTheFolder()
    {
        var src = Src("payload");
        Muzan(Path.Combine(src, "Muzan"));
        var inspect = Inspect(Zip(src, "Muzan.zip"));
        inspect.Items[0].SelectedDef = "Muzan_AI.def";

        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);

        Assert.True(Directory.Exists(Chars("Muzan")));
        Assert.False(Directory.Exists(Chars("Muzan_AI")));
        Assert.Equal("Muzan_AI.def", _store.Get(_root, "Muzan"));
        var entry = new LibraryIndexService(primaryDefs: _store).Index(_root).Characters.Single(c => c.Id == "Muzan");
        Assert.Equal("chars/Muzan/Muzan_AI.def", entry.DefPath);
    }

    [Fact]
    public void KeepingTheResolversDefSavesNothing()
    {
        var src = Src("payload");
        Muzan(Path.Combine(src, "Muzan"));
        var inspect = Inspect(Zip(src, "Muzan.zip"));
        inspect.Items[0].SelectedDef = "Muzan.def";

        _installer.Execute(inspect.Items, _root);

        Assert.Null(_store.Get(_root, "Muzan"));
    }

    // ---- collisions -------------------------------------------------------------------------------

    [Fact]
    public void SameDestinationTwiceInOneBatchMustBeRenamedOrSkipped()
    {
        var a = Src("a");
        Def(Path.Combine(a, "Muzan"), "Muzan.def", "Muzan A");
        var b = Src("b");
        Def(Path.Combine(b, "Muzan"), "Muzan.def", "Muzan B");
        var zipA = Zip(a, "Muzan.zip");
        var zipB = Zip(b, "Muzan (older).zip");

        var inspect = Inspect(zipA, zipB);
        Assert.Null(inspect.Items[0].Problem);
        Assert.Contains("also goes to chars/Muzan", inspect.Items[1].Problem);

        Assert.Null(InstallPlanRules.Rename(inspect.Items[1], "Muzan (alt)", _root));
        InstallPlanRules.Refresh(inspect.Items, _root);
        Assert.Null(inspect.Items[1].Problem);

        Assert.Equal(2, _installer.Execute(inspect.Items, _root).InstalledCount);
        Assert.Contains("Muzan B", File.ReadAllText(Chars("Muzan (alt)/Muzan.def")));
        Assert.Contains("Muzan A", File.ReadAllText(Chars("Muzan/Muzan.def")));
    }

    [Fact]
    public void SkippingOneOfTwoSameDestinationItemsClearsTheConflict()
    {
        var a = Src("a");
        Def(Path.Combine(a, "Muzan"), "Muzan.def", "Muzan A");
        var b = Src("b");
        Def(Path.Combine(b, "Muzan"), "Muzan.def", "Muzan B");
        var inspect = Inspect(Zip(a, "one.zip"), Zip(b, "two.zip"));

        inspect.Items[0].Decision = InstallItemDecision.Skip;
        var result = _installer.Execute(inspect.Items, _root);

        Assert.Equal(InstallItemOutcome.Skipped, result.Items[0].Outcome);
        Assert.Equal(InstallItemOutcome.Installed, result.Items[1].Outcome);
        Assert.Contains("Muzan B", File.ReadAllText(Chars("Muzan/Muzan.def")));
    }

    [Fact]
    public void ExistingDestinationOffersRenameAlongsideReplaceAndSkip()
    {
        var src = Src("payload");
        Def(Path.Combine(src, "Muzan"), "Muzan.def", "Muzan");
        var zip = Zip(src, "Muzan.zip");
        _installer.Execute(Inspect(zip).Items, _root);

        var again = Inspect(zip);
        var item = again.Items[0];
        Assert.True(item.DestinationExists);
        Assert.Equal(InstallItemDecision.NeedsDecision, item.Decision);

        Assert.NotNull(InstallPlanRules.Rename(item, "Muzan, v2", _root));
        Assert.Equal("Muzan", item.DestinationName);
        Assert.NotNull(InstallPlanRules.Rename(item, "CON", _root));

        Assert.Null(InstallPlanRules.Rename(item, "Muzan", _root));
        Assert.Equal(InstallItemDecision.NeedsDecision, item.Decision);

        Assert.Null(InstallPlanRules.Rename(item, "Muzan 2", _root));
        Assert.Equal(InstallItemDecision.InstallNew, item.Decision);
        Assert.Equal(1, _installer.Execute(again.Items, _root).InstalledCount);
        Assert.True(File.Exists(Chars("Muzan/Muzan.def")));
        Assert.True(File.Exists(Chars("Muzan 2/Muzan.def")));
    }

    [Fact]
    public void AnArchiveNameSelectDefCannotListIsKeptButMustBeRenamed()
    {
        var src = Src("flat");
        Def(src, "char.def", "Muzan");
        var inspect = Inspect(Zip(src, "Muzan, v2.zip"));
        var item = inspect.Items[0];

        Assert.Equal("Muzan, v2", item.DestinationName);
        Assert.Contains("','", item.Problem);
        Assert.Equal(InstallItemOutcome.Rejected, _installer.Execute(inspect.Items, _root).Items[0].Outcome);

        Assert.Null(InstallPlanRules.Rename(item, "Muzan v2", _root));
        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);
    }

    [Fact]
    public void ReplaceWarnsAboutRosterDefsTheNewVersionLacksAndDropsAStaleChoice()
    {
        var v1 = Src("v1");
        Muzan(Path.Combine(v1, "Muzan"));
        var first = Inspect(Zip(v1, "Muzan.zip"));
        first.Items[0].SelectedDef = "Muzan_AI.def";
        _installer.Execute(first.Items, _root);
        File.WriteAllText(Path.Combine(_root, "data", "select.def"), "[Characters]\nkfm\nMuzan/Muzan_AI.def\n\n[ExtraStages]\n");

        var v2 = Src("v2");
        Def(Path.Combine(v2, "Muzan"), "Muzan.def", "Demon King Muzan");
        var replace = Inspect(Zip(v2, "Muzan v2.zip"));
        var item = replace.Items[0];

        Assert.True(item.DestinationExists);
        Assert.Contains(item.ReplaceWarnings, w => w.Contains("Muzan/Muzan_AI.def"));

        item.Decision = InstallItemDecision.Replace;
        Assert.Equal(1, _installer.Execute(replace.Items, _root).InstalledCount);
        Assert.Null(_store.Get(_root, "Muzan"));
    }
}
