using System.Text;
using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Install;
using IKEMENLab.Core.Mutations;
using Xunit;

namespace IKEMENLab.Tests;

public sealed class FullgameImporterTests : IDisposable
{
    private readonly string _fixture;
    private readonly string _package;
    private readonly string _ops;
    private readonly string _bak;
    private readonly string _app;
    private readonly SafeMutationService _mutations;
    private readonly FullgameImporter _importer;

    public FullgameImporterTests()
    {
        _fixture = Path.Combine(Path.GetTempPath(), "ikemenlab-fg-" + Guid.NewGuid().ToString("N"));
        _package = Path.Combine(Path.GetTempPath(), "ikemenlab-fgpkg-" + Guid.NewGuid().ToString("N"));
        _ops = Path.Combine(Path.GetTempPath(), "ikemenlab-fg-ops-" + Guid.NewGuid().ToString("N"));
        _bak = Path.Combine(Path.GetTempPath(), "ikemenlab-fg-bak-" + Guid.NewGuid().ToString("N"));
        _app = Path.Combine(Path.GetTempPath(), "ikemenlab-fg-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_fixture, "chars"));
        Directory.CreateDirectory(Path.Combine(_fixture, "stages"));
        Directory.CreateDirectory(Path.Combine(_fixture, "data"));
        Directory.CreateDirectory(Path.Combine(_fixture, "font"));
        Directory.CreateDirectory(Path.Combine(_fixture, "sound"));
        Directory.CreateDirectory(Path.Combine(_fixture, "save"));
        Directory.CreateDirectory(_ops);
        Directory.CreateDirectory(_bak);
        Directory.CreateDirectory(_app);
        File.WriteAllText(Path.Combine(_fixture, "data", "select.def"), "; keep roster\n");
        File.WriteAllText(Path.Combine(_fixture, "save", "config.ini"), "[Config]\nMotif = data/system.def\nMasterVolume = 50\n");
        File.WriteAllBytes(Path.Combine(_fixture, "Ikemen_GO.exe"), [0]);
        Directory.CreateDirectory(Path.Combine(_fixture, "external", "mods"));
        File.WriteAllText(Path.Combine(_fixture, "external", "mods", "keep.lua"), "-- keep\n");

        BuildPackage();
        _mutations = new SafeMutationService(_ops, _bak);
        _importer = new FullgameImporter(
            new ContentInstallService(_mutations),
            _mutations,
            new CollectionStore(Path.Combine(_app, "collections.json")));
    }

    public void Dispose()
    {
        TryDelete(_fixture); TryDelete(_package); TryDelete(_ops); TryDelete(_bak); TryDelete(_app);
    }

    [Fact]
    public void DetectsFullgameManifest()
    {
        var manifest = _importer.Scan(_package, _fixture);
        Assert.True(manifest.IsFullgame);
        Assert.Equal(2, manifest.Characters.Count);
        Assert.Single(manifest.Stages);
        Assert.NotNull(manifest.Screenpack);
        Assert.NotEmpty(manifest.Fonts);
        Assert.NotEmpty(manifest.Sounds);
        Assert.StartsWith("Imported —", manifest.SuggestedCollectionName);
    }

    [Fact]
    public void NotFullgameWhenOnlyCharacters()
    {
        var only = Path.Combine(Path.GetTempPath(), "onlychars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(only, "chars", "Solo"));
        File.WriteAllText(Path.Combine(only, "chars", "Solo", "Solo.def"), CharDef("Solo"));
        File.WriteAllText(Path.Combine(only, "chars", "Solo", "x.cmd"), ";");
        File.WriteAllText(Path.Combine(only, "chars", "Solo", "x.cns"), ";");
        File.WriteAllText(Path.Combine(only, "chars", "Solo", "x.air"), ";");
        try
        {
            Assert.False(_importer.Scan(only).IsFullgame);
        }
        finally { TryDelete(only); }
    }

    [Fact]
    public void ImportCharactersStagesScreenpackFontsSounds_AndCollection()
    {
        var selectBefore = File.ReadAllText(Path.Combine(_fixture, "data", "select.def"));
        var configBefore = File.ReadAllText(Path.Combine(_fixture, "save", "config.ini"));
        var exeBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_fixture, "Ikemen_GO.exe"))));
        var jugBefore = File.ReadAllText(Path.Combine(_fixture, "external", "mods", "keep.lua"));

        var manifest = _importer.Scan(_package, _fixture);
        var result = _importer.Install(manifest, _fixture, (_, _) => FullgameDuplicateAction.Replace);

        Assert.Equal(2, result.CharactersInstalled.Count);
        Assert.Single(result.StagesInstalled);
        Assert.NotNull(result.ScreenpackInstalled);
        Assert.NotEmpty(result.FontsInstalled);
        Assert.NotEmpty(result.SoundsInstalled);
        Assert.NotNull(result.CollectionCreated);
        Assert.Equal(0, result.TotalFailed);

        Assert.True(Directory.Exists(Path.Combine(_fixture, "chars", "HeroA")));
        Assert.True(Directory.Exists(Path.Combine(_fixture, "stages", "Arena")));
        Assert.True(File.Exists(Path.Combine(_fixture, "data", result.ScreenpackInstalled!, "system.def")));
        Assert.Equal(selectBefore, File.ReadAllText(Path.Combine(_fixture, "data", "select.def")));
        Assert.Equal(configBefore, File.ReadAllText(Path.Combine(_fixture, "save", "config.ini")));
        Assert.Equal(exeBefore, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_fixture, "Ikemen_GO.exe")))));
        Assert.Equal(jugBefore, File.ReadAllText(Path.Combine(_fixture, "external", "mods", "keep.lua")));
        Assert.False(File.Exists(Path.Combine(_fixture, "chars", "Ikemen_GO.exe")));
    }

    [Fact]
    public void DuplicateSkipAndPartialFailure()
    {
        // Pre-install one character
        Directory.CreateDirectory(Path.Combine(_fixture, "chars", "HeroA"));
        File.WriteAllText(Path.Combine(_fixture, "chars", "HeroA", "HeroA.def"), CharDef("HeroA"));

        var manifest = _importer.Scan(_package, _fixture);
        Assert.Contains(manifest.Characters, c => c.FolderName == "HeroA" && c.Collision == FullgameItemCollision.Existing);

        var result = _importer.Install(manifest, _fixture, (name, _) =>
            name == "HeroA" ? FullgameDuplicateAction.Skip : FullgameDuplicateAction.Replace);

        Assert.Contains("HeroA", result.CharactersSkipped);
        Assert.Contains("HeroB", result.CharactersInstalled);
        Assert.True(result.TotalInstalled > 0);
    }

    [Fact]
    public void RefusesEngineRootAsPackage()
    {
        Assert.Throws<InvalidOperationException>(() => _importer.Scan(_fixture));
    }

    private void BuildPackage()
    {
        WriteChar(_package, "HeroA");
        WriteChar(_package, "HeroB");
        var stage = Path.Combine(_package, "stages", "Arena");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "Arena.def"), "[Info]\nname=Arena\n[StageInfo]\nautoturn=1\n[BGdef]\nspr=a.sff\n");
        File.WriteAllBytes(Path.Combine(stage, "a.sff"), [0]);

        var data = Path.Combine(_package, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "system.def"), """
            [Info]
            name = Full Pack UI
            [Files]
            spr = system.sff
            fight = fight.def
            select = select.def
            [Title Info]
            [Select Info]
            rows = 1
            columns = 1
            [VS Screen]
            [Option Info]
            """);
        File.WriteAllBytes(Path.Combine(data, "system.sff"), [0]);
        File.WriteAllText(Path.Combine(data, "fight.def"), "[Info]\nname=f\n");
        File.WriteAllText(Path.Combine(data, "select.def"), "[Characters]\n");

        Directory.CreateDirectory(Path.Combine(_package, "font"));
        File.WriteAllText(Path.Combine(_package, "font", "ui.fnt"), "fnt");
        Directory.CreateDirectory(Path.Combine(_package, "sound"));
        File.WriteAllBytes(Path.Combine(_package, "sound", "ok.wav"), [0, 1, 2]);

        // decoy engine binary in package — must never be copied
        File.WriteAllBytes(Path.Combine(_package, "Ikemen_GO.exe"), [9, 9, 9]);
    }

    private static void WriteChar(string package, string name)
    {
        var dir = Path.Combine(package, "chars", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".def"), CharDef(name));
        File.WriteAllText(Path.Combine(dir, "x.cmd"), ";");
        File.WriteAllText(Path.Combine(dir, "x.cns"), ";");
        File.WriteAllText(Path.Combine(dir, "x.air"), ";");
        File.WriteAllBytes(Path.Combine(dir, "x.sff"), [0]);
    }

    private static string CharDef(string name) =>
        $"[Info]\nname = {name}\n[Files]\nsprite = x.sff\nanim = x.air\ncmd = x.cmd\ncns = x.cns\n";

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* ignore */ }
    }
}
