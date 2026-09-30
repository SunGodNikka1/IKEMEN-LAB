using System.IO.Compression;
using IKEMENLab.Core.Install;
using IKEMENLab.Core.Mutations;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Stage package boundaries and identity. A stage keeps the folder its author supplied, and every file its
/// DEF references travels with it wherever it sits in the archive; file extensions play no part.
/// </summary>
public class StageLayoutTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "stagelayout-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ContentInstallService _installer;
    private int _seq;

    public StageLayoutTests()
    {
        _root = Path.Combine(_tmp, "ikemen");
        foreach (var d in new[] { "chars", "stages", "data", "save", "sound" }) Directory.CreateDirectory(Path.Combine(_root, d));
        File.WriteAllText(Path.Combine(_root, "data", "select.def"), "; roster\n");
        File.WriteAllText(Path.Combine(_root, "save", "config.ini"), "[Config]\nMotif = data/system.def\n");
        _installer = new ContentInstallService(new SafeMutationService(Path.Combine(_tmp, "ops"), Path.Combine(_tmp, "bak")));
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    private static string StageDef(string music, string sprite = "stage.sff") =>
        $"[Info]\nname = Test Stage\n[StageInfo]\nautoturn = 1\n[BGdef]\nspr = {sprite}\n[Music]\nbgmusic = {music}\n";

    /// <summary>Builds a zip; entries whose name ends in .def get <paramref name="def"/> as content.</summary>
    private string Zip(string name, string def, params string[] entries)
    {
        var path = Path.Combine(_tmp, name + ".zip");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var w = new StreamWriter(z.CreateEntry(entry).Open());
            w.Write(entry.EndsWith(".def", StringComparison.OrdinalIgnoreCase) ? def : "bytes:" + entry);
        }
        return path;
    }

    private (InstallPlanItem Item, InspectBatchResult Inspect) Plan(string zip)
    {
        var inspect = _installer.Inspect([zip], _root, Path.Combine(_tmp, "stg" + _seq++));
        Assert.Empty(inspect.Failures);
        return (Assert.Single(inspect.Items), inspect);
    }

    private string Install(string zip)
    {
        var (item, inspect) = Plan(zip);
        var result = _installer.Execute(inspect.Items, _root);
        Assert.True(result.InstalledCount == 1, item.Error);
        return item.DestinationName;
    }

    private bool Has(string relative) => File.Exists(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void FlatArchive_InstallsLooseWithItsMusic()
    {
        var (item, inspect) = Plan(Zip("flat", StageDef("music.mp3"), "stage.def", "stage.sff", "music.mp3"));
        Assert.True(item.Package.IsFlatStage);
        _installer.Execute(inspect.Items, _root);
        Assert.True(Has("stages/stage.def")); Assert.True(Has("stages/stage.sff")); Assert.True(Has("stages/music.mp3"));
    }

    [Fact]
    public void AuthorFolder_KeepsItsNameNotTheDefName()
    {
        var zip = Zip("af", StageDef("music.mp3"), "My Stage/stage.def", "My Stage/stage.sff", "My Stage/music.mp3");
        Assert.Equal("My Stage", Plan(zip).Item.Package.SuggestedFolderName);
        Assert.Equal("My Stage", Install(zip));
        Assert.True(Has("stages/My Stage/stage.def")); Assert.True(Has("stages/My Stage/music.mp3"));
        Assert.False(Directory.Exists(Path.Combine(_root, "stages", "stage")));
    }

    [Fact]
    public void WrapperArchive_UsesTheRealStageFolder()
    {
        Assert.Equal("My Stage", Install(Zip("w", StageDef("music.mp3"), "Release v1/My Stage/stage.def", "Release v1/My Stage/stage.sff", "Release v1/My Stage/music.mp3")));
        Assert.True(Has("stages/My Stage/music.mp3"));
        Assert.False(Directory.Exists(Path.Combine(_root, "stages", "Release v1")));
    }

    [Fact]
    public void DroppedFolder_KeepsItsNameEvenWithLooseFiles()
    {
        var dir = Path.Combine(_tmp, "Dropped Stage");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stage.def"), StageDef("music.mp3"));
        File.WriteAllText(Path.Combine(dir, "stage.sff"), "x");
        File.WriteAllText(Path.Combine(dir, "music.mp3"), "x");
        var inspect = _installer.Inspect([dir], _root, Path.Combine(_tmp, "stgd"));
        var item = Assert.Single(inspect.Items);
        Assert.False(item.Package.IsFlatStage);
        Assert.Equal("Dropped Stage", item.DestinationName);
        Assert.Equal(1, _installer.Execute(inspect.Items, _root).InstalledCount);
        Assert.True(Has("stages/Dropped Stage/music.mp3"));
    }

    [Fact]
    public void LooseFilesWithSubfolder_TakeTheArchiveName_NotTheDefName()
    {
        var zip = Zip("Cool Arena", StageDef("sound/theme.mp3"), "stage.def", "stage.sff", "sound/theme.mp3");
        var (item, _) = Plan(zip);
        Assert.Equal("Cool Arena", item.DestinationName);
        Assert.Contains(item.Package.Warnings, w => w.Contains("named after the archive"));
        Install(zip);
        Assert.True(Has("stages/Cool Arena/sound/theme.mp3"));
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("ogg")]
    [InlineData("wav")]
    public void MirroredIkemenLayout_InstallsSiblingSoundFolder_ForAnyExtension(string ext)
    {
        // stages/x.def + sound/x.<ext>: the music is a sibling of the DEF folder, not inside it.
        var (item, inspect) = Plan(Zip("mirror" + ext, StageDef($"sound/theme.{ext}"), "stages/x.def", "stages/stage.sff", $"sound/theme.{ext}"));
        Assert.True(item.Package.IsFlatStage);
        Assert.Empty(item.Package.MissingAssets);
        Assert.Single(item.Package.Companions);
        _installer.Execute(inspect.Items, _root);
        Assert.True(Has("stages/x.def")); Assert.True(Has("stages/stage.sff")); Assert.True(Has($"sound/theme.{ext}"));
    }

    [Fact]
    public void MirroredLayout_WithBareMusicName_FindsSoundFolder()
    {
        // bgmusic = theme.mp3 is found by IKEMEN in sound/, so the archive's sound/ is installed there.
        Install(Zip("bare", StageDef("theme.mp3"), "stages/x.def", "stages/stage.sff", "sound/theme.mp3"));
        Assert.True(Has("sound/theme.mp3"));
    }

    [Fact]
    public void FolderStageWithSiblingSoundFolder_MirrorsIt()
    {
        var name = Install(Zip("sib", StageDef("sound/theme.ogg"), "Pack/My Stage/stage.def", "Pack/My Stage/stage.sff", "Pack/sound/theme.ogg"));
        Assert.Equal("My Stage", name);
        Assert.True(Has("stages/My Stage/stage.def")); Assert.True(Has("sound/theme.ogg"));
    }

    [Fact]
    public void DotDotSibling_KeepsRelativePositionToTheStageFolder()
    {
        Install(Zip("dd", StageDef("../Music/theme.mp3"), "Pack/My Stage/stage.def", "Pack/My Stage/stage.sff", "Pack/Music/theme.mp3"));
        // From stages/My Stage/, ../Music/theme.mp3 is stages/Music/theme.mp3.
        Assert.True(Has("stages/Music/theme.mp3"));
    }

    [Fact]
    public void DotDotReferenceEscapingTheInstall_IsNotWritten()
    {
        var (item, _) = Plan(Zip("esc", StageDef("../../../evil.mp3"), "Pack/My Stage/stage.def", "Pack/My Stage/stage.sff", "evil.mp3"));
        Assert.Empty(item.Package.Companions);
    }

    [Fact]
    public void MissingMusic_WarnsClearly_AndListsIt()
    {
        var (item, inspect) = Plan(Zip("miss", StageDef("sound/gone.mp3"), "My Stage/stage.def", "My Stage/stage.sff"));
        Assert.Equal(["sound/gone.mp3"], item.Package.MissingAssets);
        Assert.Contains(item.Package.Warnings, w => w.Contains("Missing referenced file") && w.Contains("sound/gone.mp3"));
        Assert.True(inspect.Items[0].Package.HasMissingAssets);
    }

    [Fact]
    public void MusicAlreadyInIkemenInstall_IsNotReportedMissing()
    {
        File.WriteAllText(Path.Combine(_root, "sound", "shared.mp3"), "x");
        var (item, _) = Plan(Zip("shared", StageDef("sound/shared.mp3"), "My Stage/stage.def", "My Stage/stage.sff"));
        Assert.Empty(item.Package.MissingAssets);
    }

    [Fact]
    public void ExistingDifferentSharedFile_IsNotOverwritten_TheStageKeepsItsOwnCopy()
    {
        File.WriteAllText(Path.Combine(_root, "sound", "theme.mp3"), "someone else's music");
        var (item, inspect) = Plan(Zip("clash", StageDef("sound/theme.mp3"), "Pack/My Stage/stage.def", "Pack/My Stage/stage.sff", "Pack/sound/theme.mp3"));
        Assert.Contains(item.Package.Warnings, w => w.Contains("already exists and is different"));
        _installer.Execute(inspect.Items, _root);
        Assert.Equal("someone else's music", File.ReadAllText(Path.Combine(_root, "sound", "theme.mp3")));
        // IKEMEN looks next to the DEF first, so the stage plays its own file.
        Assert.True(Has("stages/My Stage/sound/theme.mp3"));
    }

    [Fact]
    public void NoCompanionsForIdenticalExistingFile()
    {
        var zip = Zip("same", StageDef("sound/theme.mp3"), "Pack/My Stage/stage.def", "Pack/My Stage/stage.sff", "Pack/sound/theme.mp3");
        Install(zip);
        // Second install of the same package: the shared file is already there and identical.
        var (item, _) = Plan(zip);
        Assert.Empty(item.Package.Companions);
        Assert.Empty(item.Package.MissingAssets);
    }

    [Fact]
    public void FailedCompanion_RollsBackTheWholeStage()
    {
        // A directory where the music file should go makes the companion write fail.
        Directory.CreateDirectory(Path.Combine(_root, "sound", "theme.mp3"));
        var zip = Zip("rb", StageDef("sound/theme.mp3"), "Pack/My Stage/stage.def", "Pack/My Stage/stage.sff", "Pack/sound/theme.mp3");
        var inspect = _installer.Inspect([zip], _root, Path.Combine(_tmp, "stgrb"));
        var item = Assert.Single(inspect.Items);
        var result = _installer.Execute(inspect.Items, _root);
        Assert.Equal(0, result.InstalledCount);
        Assert.False(Directory.Exists(Path.Combine(_root, "stages", "My Stage")));
        Assert.NotNull(item.Error);
    }
}
