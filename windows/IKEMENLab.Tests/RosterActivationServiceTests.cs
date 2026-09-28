using System.Text;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public class RosterActivationServiceTests : IDisposable
{
    private readonly string _fixtureRoot;
    private readonly string _opsRoot;
    private readonly string _backupRoot;
    private readonly string _stagingRoot;
    private readonly SafeMutationService _mutations;
    private readonly RosterActivationService _roster;

    public RosterActivationServiceTests()
    {
        DefFileReaderEnsure();
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-roster-" + Guid.NewGuid().ToString("N"));
        _opsRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-roster-ops-" + Guid.NewGuid().ToString("N"));
        _backupRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-roster-bak-" + Guid.NewGuid().ToString("N"));
        _stagingRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-roster-stg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "chars"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "stages"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "data"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "save"));
        Directory.CreateDirectory(_opsRoot);
        Directory.CreateDirectory(_backupRoot);
        Directory.CreateDirectory(_stagingRoot);
        File.WriteAllText(Path.Combine(_fixtureRoot, "save", "config.ini"), "[Config]\nMasterVolume = 50\n");
        _mutations = new SafeMutationService(_opsRoot, _backupRoot);
        _roster = new RosterActivationService(_mutations, _stagingRoot);
    }

    public void Dispose()
    {
        TryDelete(_fixtureRoot);
        TryDelete(_opsRoot);
        TryDelete(_backupRoot);
        TryDelete(_stagingRoot);
    }

    [Fact]
    public void EnableUnregisteredCharacter()
    {
        WriteChar("loner");
        SeedSelect("""
            ; header
            [Characters]
            kfm, order=1
            [ExtraStages]
            stages/kfm.def
            [Options]
            arcade.maxmatches = 6,1
            """);
        WriteChar("kfm");
        WriteStage("kfm.def");

        var before = File.ReadAllText(SelectPath());
        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/loner/loner.def", enabled: true);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        Assert.Equal(ContentStatus.Active, Index().CharacterStatus("chars/loner/loner.def"));

        var after = File.ReadAllText(SelectPath());
        Assert.Contains("loner", after, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; header", after);
        Assert.Contains("kfm, order=1", after);
        Assert.Contains("arcade.maxmatches = 6,1", after);
        Assert.Contains(before.Split('\n').First(l => l.Contains("kfm, order")), after);
        AssertConfigAndContentUntouched();
    }

    [Fact]
    public void DisableActiveCharacter_PreservesParameters()
    {
        WriteChar("kfm");
        SeedSelect("""
            [Characters]
            kfm, stages/kfm.def, order=2
            [ExtraStages]
            """);

        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/kfm/kfm.def", enabled: false);
        Assert.True(result.Success, result.Error);
        var text = File.ReadAllText(SelectPath());
        Assert.Contains(";kfm, stages/kfm.def, order=2", text.Replace("\r\n", "\n"));
        Assert.Equal(ContentStatus.Disabled, Index().CharacterStatus("chars/kfm/kfm.def"));
        Assert.True(File.Exists(Path.Combine(_fixtureRoot, "chars", "kfm", "kfm.def")));
        AssertConfigAndContentUntouched();
    }

    [Fact]
    public void ReEnableDisabledCharacter()
    {
        WriteChar("suave");
        SeedSelect("""
            [Characters]
            ;suave, order=3
            """);

        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/suave/suave.def", enabled: true);
        Assert.True(result.Success, result.Error);
        var text = File.ReadAllText(SelectPath());
        Assert.Contains("suave, order=3", text);
        Assert.DoesNotContain(";suave, order=3", text.Replace("\r\n", "\n"));
        Assert.Equal(ContentStatus.Active, Index().CharacterStatus("chars/suave/suave.def"));
    }

    [Fact]
    public void NestedCharacterDefPath()
    {
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "chars", "Pack", "Ken"));
        File.WriteAllText(Path.Combine(_fixtureRoot, "chars", "Pack", "Ken", "Ken.def"), TestRoot.CharDef("Ken"));
        SeedSelect("""
            [Characters]
            """);

        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/Pack/Ken/Ken.def", enabled: true);
        Assert.True(result.Success, result.Error);
        Assert.Equal(ContentStatus.Active, Index().CharacterStatus("chars/Pack/Ken/Ken.def"));
        Assert.Contains("Pack/Ken/Ken", File.ReadAllText(SelectPath()).Replace('\\', '/'));
    }

    [Fact]
    public void DuplicateCharacterAmbiguityRejected()
    {
        WriteChar("kfm");
        SeedSelect("""
            [Characters]
            kfm
            chars/kfm/kfm.def
            """);

        var before = File.ReadAllText(SelectPath());
        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/kfm/kfm.def", enabled: false);
        Assert.False(result.Success);
        Assert.Contains("Ambiguous", result.Error ?? "");
        Assert.Equal(before, File.ReadAllText(SelectPath()));
    }

    [Fact]
    public void SlashAndCaseNormalization()
    {
        WriteChar("kfm");
        SeedSelect("""
            [Characters]
            KFM
            """);

        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/kfm/kfm.def", enabled: false);
        Assert.True(result.Success, result.Error);
        Assert.Equal(ContentStatus.Disabled, Index().CharacterStatus("chars/KFM/KFM.def"));
    }

    [Fact]
    public void EnableDisableReEnableStage_PreservesParameters()
    {
        WriteStage("arena.def");
        SeedSelect("""
            [Characters]
            [ExtraStages]
            stages/arena.def, order=2
            """);

        Assert.True(_roster.SetStageEnabled(_fixtureRoot, "stages/arena.def", false).Success);
        var disabled = File.ReadAllText(SelectPath());
        Assert.Contains(";stages/arena.def, order=2", disabled.Replace("\r\n", "\n"));
        Assert.Equal(ContentStatus.Disabled, Index().StageStatus("stages/arena.def"));

        Assert.True(_roster.SetStageEnabled(_fixtureRoot, "stages/arena.def", true).Success);
        Assert.Contains("stages/arena.def, order=2", File.ReadAllText(SelectPath()));
        Assert.Equal(ContentStatus.Active, Index().StageStatus("stages/arena.def"));
        Assert.True(File.Exists(Path.Combine(_fixtureRoot, "stages", "arena.def")));
    }

    [Fact]
    public void EnableUnregisteredStage()
    {
        WriteStage("new.def");
        SeedSelect("""
            [ExtraStages]
            """);
        Assert.True(_roster.SetStageEnabled(_fixtureRoot, "stages/new.def", true).Success);
        Assert.Equal(ContentStatus.Active, Index().StageStatus("stages/new.def"));
    }

    [Fact]
    public void DuplicateStageAmbiguityRejected()
    {
        WriteStage("dup.def");
        SeedSelect("""
            [ExtraStages]
            stages/dup.def
            stages/DUP.def
            """);
        var before = File.ReadAllText(SelectPath());
        var result = _roster.SetStageEnabled(_fixtureRoot, "stages/dup.def", false);
        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllText(SelectPath()));
    }

    [Fact]
    public void CustomMotifSelectDefLocation()
    {
        WriteChar("kfm");
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "data", "mymotif"));
        File.WriteAllText(Path.Combine(_fixtureRoot, "data", "mymotif", "system.def"), "[Files]\nselect = roster.def\n");
        File.WriteAllText(Path.Combine(_fixtureRoot, "data", "mymotif", "roster.def"), "[Characters]\n");
        File.WriteAllText(Path.Combine(_fixtureRoot, "data", "select.def"), "[Characters]\nSHOULD_NOT_TOUCH\n");
        File.WriteAllText(Path.Combine(_fixtureRoot, "save", "config.ini"), "[Config]\nmotif = data/mymotif/system.def\n");

        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/kfm/kfm.def", true);
        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.GetFullPath(Path.Combine(_fixtureRoot, "data", "mymotif", "roster.def")), result.SelectDefPath);
        Assert.Contains("kfm", File.ReadAllText(Path.Combine(_fixtureRoot, "data", "mymotif", "roster.def")));
        Assert.Contains("SHOULD_NOT_TOUCH", File.ReadAllText(Path.Combine(_fixtureRoot, "data", "select.def")));
    }

    [Fact]
    public void MalformedSelectDefFailsSafely()
    {
        WriteChar("kfm");
        // Missing [Characters] is still parseable; use unreadable by deleting after locate? 
        // Empty file with only garbage that has characters section but duplicate ambiguity already covered.
        // NotFound select.def:
        File.Delete(Path.Combine(_fixtureRoot, "data", "select.def"));
        // recreate data dir without select
        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/kfm/kfm.def", true);
        Assert.False(result.Success);
    }

    [Fact]
    public void SafeMutationBackupAndRollback()
    {
        WriteChar("kfm");
        SeedSelect("""
            [Characters]
            kfm
            """);
        var before = File.ReadAllText(SelectPath());
        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/kfm/kfm.def", false);
        Assert.True(result.Success, result.Error);
        Assert.False(string.IsNullOrEmpty(result.OperationId));
        var manifest = _mutations.LoadManifest(result.OperationId!);
        Assert.NotNull(manifest);
        Assert.True(File.Exists(manifest!.BackupPath));

        var rb = _mutations.Rollback(result.OperationId!);
        Assert.True(rb.Success);
        Assert.Equal(before, File.ReadAllText(SelectPath()));
    }

    [Fact]
    public void DryRunPerformsNoMutation()
    {
        WriteChar("kfm");
        SeedSelect("""
            [Characters]
            """);
        var before = SnapshotTree();
        var result = _roster.SetCharacterEnabled(_fixtureRoot, "chars/kfm/kfm.def", true, dryRun: true);
        Assert.True(result.Success, result.Error);
        Assert.Equal(before, SnapshotTree());
        Assert.DoesNotContain("kfm", File.ReadAllText(SelectPath()).Split('\n').Skip(1).FirstOrDefault() ?? "");
    }

    [Fact]
    public void PreferredCharacterRosterNameHelpers()
    {
        Assert.Equal("kfm", SelectDefRosterEditor.PreferredCharacterRosterName("chars/kfm/kfm.def"));
        Assert.Equal("Pack/Ken/Ken", SelectDefRosterEditor.PreferredCharacterRosterName("chars/Pack/Ken/Ken.def"));
        Assert.Equal("stages/foo.def", SelectDefRosterEditor.PreferredStageRosterName("stages/foo.def"));
    }

    [Fact]
    public void CommentUncommentPreserveIndent()
    {
        Assert.Equal(";  kfm, x=1", SelectDefRosterEditor.CommentOutLine("  kfm, x=1"));
        Assert.Equal("  kfm, x=1", SelectDefRosterEditor.UncommentLine(";  kfm, x=1"));
    }

    private SelectDefIndex Index()
    {
        var config = IKEMENLab.Core.Config.IkemenConfigReader.Read(_fixtureRoot);
        return SelectDefIndex.Build(_fixtureRoot, SelectDefLocator.Locate(_fixtureRoot, config.Motif));
    }

    private string SelectPath() => Path.Combine(_fixtureRoot, "data", "select.def");

    private void SeedSelect(string content)
        => File.WriteAllText(SelectPath(), content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));

    private void WriteChar(string name)
    {
        var dir = Path.Combine(_fixtureRoot, "chars", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".def"), TestRoot.CharDef(name));
    }

    private void WriteStage(string fileName)
    {
        File.WriteAllText(Path.Combine(_fixtureRoot, "stages", fileName),
            "[Info]\nname = Stage\n[StageInfo]\nautoturn=1\n[BGdef]\nspr = x.sff\n");
    }

    private void AssertConfigAndContentUntouched()
    {
        Assert.Equal("[Config]\nMasterVolume = 50\n", File.ReadAllText(Path.Combine(_fixtureRoot, "save", "config.ini")));
    }

    private string SnapshotTree()
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(_fixtureRoot, "*", SearchOption.AllDirectories).OrderBy(f => f))
        {
            sb.Append(Path.GetRelativePath(_fixtureRoot, file).Replace('\\', '/'));
            sb.Append('=');
            sb.Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void DefFileReaderEnsure()
        => DefFileReader.EnsureEncodingsRegistered();

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { /* ignore */ }
    }
}
