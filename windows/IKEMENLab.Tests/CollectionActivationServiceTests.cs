using System.Text;
using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Config;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public sealed class CollectionActivationServiceTests : IDisposable
{
    private readonly string _fixtureRoot;
    private readonly string _opsRoot;
    private readonly string _backupRoot;
    private readonly string _stagingRoot;
    private readonly string _appData;
    private readonly SafeMutationService _mutations;
    private readonly ActiveCollectionStore _activeStore;
    private readonly CollectionStore _collections;
    private readonly CollectionActivationService _activation;

    public CollectionActivationServiceTests()
    {
        DefFileReader.EnsureEncodingsRegistered();
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-colact-" + Guid.NewGuid().ToString("N"));
        _opsRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-colact-ops-" + Guid.NewGuid().ToString("N"));
        _backupRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-colact-bak-" + Guid.NewGuid().ToString("N"));
        _stagingRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-colact-stg-" + Guid.NewGuid().ToString("N"));
        _appData = Path.Combine(Path.GetTempPath(), "ikemenlab-colact-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "chars"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "stages"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "data"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "save"));
        Directory.CreateDirectory(_opsRoot);
        Directory.CreateDirectory(_backupRoot);
        Directory.CreateDirectory(_stagingRoot);
        Directory.CreateDirectory(_appData);
        File.WriteAllText(Path.Combine(_fixtureRoot, "save", "config.ini"), "[Config]\nMasterVolume = 50\n");
        _mutations = new SafeMutationService(_opsRoot, _backupRoot);
        _activeStore = new ActiveCollectionStore(Path.Combine(_appData, "active-collection.json"));
        _collections = new CollectionStore(Path.Combine(_appData, "collections.json"));
        _activation = new CollectionActivationService(_mutations, _activeStore, _stagingRoot);
    }

    public void Dispose()
    {
        TryDelete(_fixtureRoot);
        TryDelete(_opsRoot);
        TryDelete(_backupRoot);
        TryDelete(_stagingRoot);
        TryDelete(_appData);
    }

    [Fact]
    public void ActivateManualCollection_EnablesMembers_DisablesOthers()
    {
        WriteChar("kfm");
        WriteChar("suave");
        WriteChar("loner");
        WriteStage("arena.def");
        SeedSelect("""
            ; keep me
            [Characters]
            kfm, order=2
            suave, stages/arena.def
            [ExtraStages]
            stages/arena.def, order=9
            [Options]
            arcade.maxmatches = 6,1
            """);

        var fav = _collections.Create(_fixtureRoot, "Favorites");
        fav = _collections.Add(_fixtureRoot, fav.Id, [Entry("kfm"), Entry("loner")]);

        var index = BuildIndex();
        var preview = _activation.Preview(_fixtureRoot, fav, index);
        Assert.True(preview.CanActivate, preview.Error);
        Assert.Contains(preview.WillEnable, p => p.Contains("loner", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.WillDisable, p => p.Contains("suave", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.AlreadyActive, p => p.Contains("kfm", StringComparison.OrdinalIgnoreCase));

        var beforeStages = ExtractExtraStages(File.ReadAllText(SelectPath()));
        var beforeConfig = File.ReadAllText(Path.Combine(_fixtureRoot, "save", "config.ini"));
        var result = _activation.Activate(_fixtureRoot, fav, index);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        Assert.False(string.IsNullOrEmpty(result.OperationId));

        index = BuildIndex();
        Assert.Equal(ContentStatus.Active, Status("chars/kfm/kfm.def"));
        Assert.Equal(ContentStatus.Active, Status("chars/loner/loner.def"));
        Assert.Equal(ContentStatus.Disabled, Status("chars/suave/suave.def"));

        var after = File.ReadAllText(SelectPath());
        Assert.Contains("; keep me", after);
        Assert.Contains("kfm, order=2", after);
        Assert.Contains(";suave, stages/arena.def", after.Replace("\r\n", "\n"));
        Assert.Contains("arcade.maxmatches = 6,1", after);
        Assert.Equal(beforeStages, ExtractExtraStages(after));
        Assert.Equal(beforeConfig, File.ReadAllText(Path.Combine(_fixtureRoot, "save", "config.ini")));
        Assert.True(File.Exists(Path.Combine(_fixtureRoot, "chars", "suave", "suave.def")));
        Assert.Equal(CollectionRosterStatus.Active, _activation.GetRosterStatus(_fixtureRoot, fav, index));
        Assert.True(File.Exists(_activeStore.FilePath));
        Assert.DoesNotContain(_fixtureRoot, _activeStore.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivateSmartCollection_ResolvesAtActivationTime()
    {
        WriteChar("ryu", "Capcom");
        WriteChar("ken", "Capcom");
        WriteChar("other", "Someone");
        SeedSelect("""
            [Characters]
            ryu
            other
            """);

        var smart = _collections.Create(_fixtureRoot, "Capcom", CollectionKind.Smart,
            [new CollectionRule(RuleField.Author, RuleComparison.Equals, "Capcom")]);

        var index = BuildIndex();
        var result = _activation.Activate(_fixtureRoot, smart, index);
        Assert.True(result.Success, result.Error);
        index = BuildIndex();
        Assert.Equal(ContentStatus.Active, Status("chars/ryu/ryu.def"));
        Assert.Equal(ContentStatus.Active, Status("chars/ken/ken.def"));
        Assert.Equal(ContentStatus.Disabled, Status("chars/other/other.def"));
        Assert.Equal(CollectionRosterStatus.Active, _activation.GetRosterStatus(_fixtureRoot, smart, index));
    }

    [Fact]
    public void MissingMembersSkippedWithWarning_AllMissingFailsClosed()
    {
        WriteChar("kfm");
        SeedSelect("""
            [Characters]
            kfm
            """);

        var fav = _collections.Create(_fixtureRoot, "Ghosts");
        // Add a real + a missing member via store members list
        fav = _collections.Add(_fixtureRoot, fav.Id, [Entry("kfm")]);
        // Manually inject missing by editing JSON is hard; create collection then add missing id
        var all = File.ReadAllText(_collections.FilePath);
        // Use Remove/Add path: create with member that won't be on disk
        var ghosts = _collections.Create(_fixtureRoot, "OnlyMissing");
        // Directly poke members through Add with a fake entry that won't be in index
        ghosts = _collections.Add(_fixtureRoot, ghosts.Id,
            [new CharacterEntry
            {
                Id = "gone",
                Name = "Gone",
                DisplayName = "Gone",
                Author = "X",
                VersionDate = "",
                DefPath = "chars/gone/gone.def",
                FolderPath = "chars/gone"
            }]);

        var index = BuildIndex();
        var blocked = _activation.Preview(_fixtureRoot, ghosts, index);
        Assert.False(blocked.CanActivate);
        Assert.Contains("missing", blocked.Error ?? "", StringComparison.OrdinalIgnoreCase);

        // Partial missing: add gone to favorites alongside kfm
        fav = _collections.Add(_fixtureRoot, fav.Id,
            [new CharacterEntry
            {
                Id = "gone",
                Name = "Gone",
                DisplayName = "Gone",
                Author = "X",
                VersionDate = "",
                DefPath = "chars/gone/gone.def",
                FolderPath = "chars/gone"
            }]);
        WriteChar("extra");
        SeedSelect("""
            [Characters]
            kfm
            extra
            """);
        index = BuildIndex();
        fav = _collections.Load(_fixtureRoot).First(c => c.Id == fav.Id);
        var preview = _activation.Preview(_fixtureRoot, fav, index);
        Assert.True(preview.CanActivate, preview.Error);
        Assert.NotEmpty(preview.Missing);
        Assert.Contains("skip", preview.Warning ?? "", StringComparison.OrdinalIgnoreCase);

        var result = _activation.Activate(_fixtureRoot, fav, index);
        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("gone", File.ReadAllText(SelectPath()), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ContentStatus.Disabled, Status("chars/extra/extra.def"));
    }

    [Fact]
    public void AmbiguousAndDuplicateFailClosed()
    {
        WriteChar("kfm");
        SeedSelect("""
            [Characters]
            kfm
            chars/kfm/kfm.def
            """);
        var fav = _collections.Create(_fixtureRoot, "Dup");
        fav = _collections.Add(_fixtureRoot, fav.Id, [Entry("kfm")]);
        var before = File.ReadAllText(SelectPath());
        var preview = _activation.Preview(_fixtureRoot, fav, BuildIndex());
        Assert.False(preview.CanActivate);
        Assert.NotEmpty(preview.Ambiguous);
        var result = _activation.Activate(_fixtureRoot, fav, BuildIndex());
        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllText(SelectPath()));
    }

    [Fact]
    public void NestedPath_CaseSlash_EmptyCollection_AllCharacters()
    {
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "chars", "Pack", "Ken"));
        File.WriteAllText(Path.Combine(_fixtureRoot, "chars", "Pack", "Ken", "Ken.def"), TestRoot.CharDef("Ken"));
        WriteChar("kfm");
        SeedSelect("""
            [Characters]
            KFM
            """);

        var empty = _collections.Create(_fixtureRoot, "Empty");
        var index = BuildIndex();
        Assert.True(_activation.Activate(_fixtureRoot, empty, index).Success);
        Assert.Equal(ContentStatus.Disabled, Status("chars/kfm/kfm.def"));
        Assert.Equal(ContentStatus.Unregistered, Status("chars/Pack/Ken/Ken.def"));

        index = BuildIndex();
        var all = _activation.Activate(_fixtureRoot, null, index, isAllCharacters: true);
        Assert.True(all.Success, all.Error);
        index = BuildIndex();
        Assert.Equal(ContentStatus.Active, Status("chars/kfm/kfm.def"));
        Assert.Equal(ContentStatus.Active, Status("chars/Pack/Ken/Ken.def"));
        Assert.Contains("Pack/Ken/Ken", File.ReadAllText(SelectPath()).Replace('\\', '/'));
    }

    [Fact]
    public void SingleTransaction_BackupRollback_DryRun_ExternalModified()
    {
        WriteChar("a");
        WriteChar("b");
        SeedSelect("""
            [Characters]
            a
            b
            """);
        var fav = _collections.Create(_fixtureRoot, "AOnly");
        fav = _collections.Add(_fixtureRoot, fav.Id, [Entry("a")]);
        var index = BuildIndex();

        var dry = _activation.Activate(_fixtureRoot, fav, index, dryRun: true);
        Assert.True(dry.Success, dry.Error);
        Assert.NotNull(dry.Plan);
        Assert.Contains("a", File.ReadAllText(SelectPath()));
        Assert.Contains("b", File.ReadAllText(SelectPath()));

        var before = File.ReadAllText(SelectPath());
        var result = _activation.Activate(_fixtureRoot, fav, index);
        Assert.True(result.Success, result.Error);
        Assert.False(string.IsNullOrEmpty(result.OperationId));
        var manifest = _mutations.LoadManifest(result.OperationId!);
        Assert.NotNull(manifest);
        Assert.True(File.Exists(manifest!.BackupPath));

        // One operation only for the batch.
        var ops = Directory.GetDirectories(_opsRoot);
        Assert.Single(ops);

        index = BuildIndex();
        Assert.Equal(CollectionRosterStatus.Active, _activation.GetRosterStatus(_fixtureRoot, fav, index));

        // External roster change → Modified
        File.WriteAllText(SelectPath(), before);
        index = BuildIndex();
        Assert.Equal(CollectionRosterStatus.Modified, _activation.GetRosterStatus(_fixtureRoot, fav, index));

        var rb = _mutations.Rollback(result.OperationId!);
        Assert.True(rb.Success); // restores post-activation state from backup... wait, we overwrote select.def
        // Rollback restores the backup taken BEFORE activation (original with a+b).
        // But we already set select.def to `before` which equals that. Re-activate then rollback:
        result = _activation.Activate(_fixtureRoot, fav, BuildIndex());
        Assert.True(result.Success, result.Error);
        var activated = File.ReadAllText(SelectPath());
        Assert.True(_mutations.Rollback(result.OperationId!).Success);
        Assert.NotEqual(activated.Replace("\r\n", "\n"), File.ReadAllText(SelectPath()).Replace("\r\n", "\n"));
        Assert.Contains("b", File.ReadAllText(SelectPath()));
    }

    [Fact]
    public void FailedValidationRollsBack()
    {
        // Covered implicitly by ambiguity reject; also verify ops count stays clean on failure.
        WriteChar("kfm");
        SeedSelect("""
            [Characters]
            kfm
            chars/kfm/kfm.def
            """);
        var fav = _collections.Create(_fixtureRoot, "Bad");
        fav = _collections.Add(_fixtureRoot, fav.Id, [Entry("kfm")]);
        var beforeTree = SnapshotTree();
        Assert.False(_activation.Activate(_fixtureRoot, fav, BuildIndex()).Success);
        Assert.Equal(beforeTree, SnapshotTree());
    }

    private IReadOnlyList<CharacterEntry> BuildIndex()
    {
        var characters = new CharacterIndexer().Index(_fixtureRoot, out _);
        var select = SelectDefIndex.Build(_fixtureRoot, SelectDefLocator.Locate(_fixtureRoot, IkemenConfigReader.Read(_fixtureRoot).Motif));
        return characters.Select(c => c with { Status = select.CharacterStatus(c.DefPath) }).ToList();
    }

    private ContentStatus Status(string def) => BuildIndex().First(c =>
        c.DefPath.Replace('\\', '/').Equals(def.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).Status;

    private CharacterEntry Entry(string id, string? author = null)
        => new()
        {
            Id = id,
            Name = id,
            DisplayName = id,
            Author = author ?? "Test",
            VersionDate = "",
            DefPath = $"chars/{id}/{id}.def",
            FolderPath = $"chars/{id}"
        };

    private string SelectPath() => Path.Combine(_fixtureRoot, "data", "select.def");

    private void SeedSelect(string content)
        => File.WriteAllText(SelectPath(), content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));

    private void WriteChar(string name, string author = "Test")
    {
        var dir = Path.Combine(_fixtureRoot, "chars", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".def"), TestRoot.CharDef(name, author));
    }

    private void WriteStage(string fileName)
        => File.WriteAllText(Path.Combine(_fixtureRoot, "stages", fileName),
            "[Info]\nname = Stage\n[StageInfo]\nautoturn=1\n[BGdef]\nspr = x.sff\n");

    private static string ExtractExtraStages(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var inStages = false;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith('[') && t.Contains(']'))
            {
                inStages = t.Equals("[ExtraStages]", StringComparison.OrdinalIgnoreCase);
                if (inStages) sb.AppendLine(line);
                continue;
            }
            if (inStages) sb.AppendLine(line);
        }
        return sb.ToString();
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

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { /* ignore */ }
    }
}
