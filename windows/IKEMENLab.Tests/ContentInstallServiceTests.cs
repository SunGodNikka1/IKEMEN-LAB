using System.IO.Compression;
using System.Text;
using IKEMENLab.Core.Install;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public class ContentInstallServiceTests : IDisposable
{
    private readonly string _fixtureRoot;
    private readonly string _opsRoot;
    private readonly string _backupRoot;
    private readonly string _stagingRoot;
    private readonly SafeMutationService _mutations;
    private readonly ContentInstallService _installer;

    public ContentInstallServiceTests()
    {
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-install-" + Guid.NewGuid().ToString("N"));
        _opsRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-install-ops-" + Guid.NewGuid().ToString("N"));
        _backupRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-install-bak-" + Guid.NewGuid().ToString("N"));
        _stagingRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-install-stg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "chars"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "stages"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "data"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "save"));
        Directory.CreateDirectory(_opsRoot);
        Directory.CreateDirectory(_backupRoot);
        Directory.CreateDirectory(_stagingRoot);
        File.WriteAllText(Path.Combine(_fixtureRoot, "data", "select.def"), "; roster\n");
        File.WriteAllText(Path.Combine(_fixtureRoot, "save", "config.ini"), "[Config]\nMasterVolume = 50\n");
        _mutations = new SafeMutationService(_opsRoot, _backupRoot);
        _installer = new ContentInstallService(_mutations);
    }

    public void Dispose()
    {
        TryDelete(_fixtureRoot);
        TryDelete(_opsRoot);
        TryDelete(_backupRoot);
        TryDelete(_stagingRoot);
    }

    [Fact]
    public void ZipCharacterInstall()
    {
        var zip = CreateCharacterZip("ZipHero");
        var inspect = _installer.Inspect([zip], _fixtureRoot, _stagingRoot);
        Assert.Single(inspect.Items);
        Assert.Equal(InstallContentKind.Character, inspect.Items[0].Package.Kind);
        Assert.False(inspect.Items[0].DestinationExists);

        var result = _installer.Execute(inspect.Items, _fixtureRoot);
        Assert.Equal(1, result.InstalledCount);
        Assert.True(File.Exists(Path.Combine(_fixtureRoot, "chars", "ZipHero", "ZipHero.def")));
        AssertSelectAndConfigUnchanged();
        _installer.CleanupStaging(inspect.StagingDirectories);
    }

    [Fact]
    public void ExtractedFolderCharacterInstall()
    {
        var folder = CreateCharacterFolder("FolderHero");
        try
        {
            var inspect = _installer.Inspect([folder], _fixtureRoot, _stagingRoot);
            Assert.Single(inspect.Items);
            var result = _installer.Execute(inspect.Items, _fixtureRoot);
            Assert.Equal(1, result.InstalledCount);
            Assert.True(Directory.Exists(Path.Combine(_fixtureRoot, "chars", "FolderHero")));
            AssertSelectAndConfigUnchanged();
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(folder)!);
        }
    }

    [Fact]
    public void NestedWrapperFolderCharacter()
    {
        var outer = Path.Combine(Path.GetTempPath(), "wrap-" + Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(outer, "payload", "NestedHero");
        Directory.CreateDirectory(inner);
        WriteCharacterFiles(inner, "NestedHero");
        try
        {
            var inspect = _installer.Inspect([outer], _fixtureRoot, _stagingRoot);
            Assert.Single(inspect.Items);
            Assert.Equal("NestedHero", inspect.Items[0].Package.SuggestedFolderName);
            var result = _installer.Execute(inspect.Items, _fixtureRoot);
            Assert.Equal(1, result.InstalledCount);
            Assert.True(File.Exists(Path.Combine(_fixtureRoot, "chars", "NestedHero", "NestedHero.def")));
        }
        finally
        {
            TryDelete(outer);
        }
    }

    [Fact]
    public void StageFolderInstallWithCompanionAssets()
    {
        var parent = Path.Combine(Path.GetTempPath(), "stagepkg-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(parent, "Arena");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Arena.def"),
            "[Info]\nname = Arena\n\n[StageInfo]\nautoturn = 1\n\n[BGdef]\nspr = Arena.sff\n\n[BG 0]\ntype = normal\n");
        File.WriteAllBytes(Path.Combine(folder, "Arena.sff"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "theme.ogg"), [4, 5]);
        try
        {
            var inspect = _installer.Inspect([parent], _fixtureRoot, _stagingRoot);
            Assert.Contains(inspect.Items, i => i.Package.Kind == InstallContentKind.Stage);
            var item = inspect.Items.First(i => i.Package.Kind == InstallContentKind.Stage);
            item.Decision = InstallItemDecision.InstallNew;
            var result = _installer.Execute([item], _fixtureRoot);
            Assert.Equal(1, result.InstalledCount);
            Assert.True(File.Exists(Path.Combine(_fixtureRoot, "stages", "Arena", "Arena.def")));
            Assert.True(File.Exists(Path.Combine(_fixtureRoot, "stages", "Arena", "Arena.sff")));
            Assert.True(File.Exists(Path.Combine(_fixtureRoot, "stages", "Arena", "theme.ogg")));
            AssertSelectAndConfigUnchanged();
        }
        finally
        {
            TryDelete(parent);
        }
    }

    [Fact]
    public void FlatStageInstall()
    {
        var folder = Path.Combine(Path.GetTempPath(), "flatstage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "FlatStage.def"),
            "[StageInfo]\nautoturn = 1\n\n[BGdef]\nspr = FlatStage.sff\n");
        File.WriteAllBytes(Path.Combine(folder, "FlatStage.sff"), [9]);
        try
        {
            var inspect = _installer.Inspect([folder], _fixtureRoot, _stagingRoot);
            Assert.Single(inspect.Items);
            Assert.Contains("Flat stage layout", string.Join(" ", inspect.Items[0].Package.Warnings));
            var result = _installer.Execute(inspect.Items, _fixtureRoot);
            Assert.Equal(1, result.InstalledCount);
            Assert.True(File.Exists(Path.Combine(_fixtureRoot, "stages", "FlatStage.def")));
            Assert.True(File.Exists(Path.Combine(_fixtureRoot, "stages", "FlatStage.sff")));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public void InvalidContentRejected()
    {
        var folder = Path.Combine(Path.GetTempPath(), "junk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "hello");
        try
        {
            var inspect = _installer.Inspect([folder], _fixtureRoot, _stagingRoot);
            Assert.Empty(inspect.Items);
            Assert.NotEmpty(inspect.Failures);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public void AmbiguousCharacterAndStageRejected()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ambi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        WriteCharacterFiles(folder, "Both");
        File.WriteAllText(Path.Combine(folder, "stage.def"),
            "[StageInfo]\nautoturn = 1\n\n[BGdef]\nspr = x.sff\n");
        try
        {
            var inspect = _installer.Inspect([folder], _fixtureRoot, _stagingRoot);
            Assert.Empty(inspect.Items);
            Assert.Contains(inspect.Failures, f => f.Reason.Contains("Ambiguous", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public void ArchiveTraversalRejected()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "trav-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escape.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("nope");
        }

        try
        {
            var inspect = _installer.Inspect([zipPath], _fixtureRoot, _stagingRoot);
            Assert.Empty(inspect.Items);
            Assert.NotEmpty(inspect.Failures);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public void DuplicateCharacterRequiresDecision_ReplaceBackupRollback()
    {
        var parent1 = Path.Combine(Path.GetTempPath(), "dup1-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(parent1, "DupHero");
        Directory.CreateDirectory(folder);
        WriteCharacterFiles(folder, "DupHero");

        var first = _installer.Inspect([folder], _fixtureRoot, _stagingRoot);
        Assert.Equal(1, _installer.Execute(first.Items, _fixtureRoot).InstalledCount);
        File.WriteAllText(Path.Combine(_fixtureRoot, "chars", "DupHero", "marker.txt"), "original");

        var parent2 = Path.Combine(Path.GetTempPath(), "dup2-" + Guid.NewGuid().ToString("N"));
        var folder2 = Path.Combine(parent2, "DupHero");
        Directory.CreateDirectory(folder2);
        WriteCharacterFiles(folder2, "DupHero");
        File.WriteAllText(Path.Combine(folder2, "new.txt"), "replacement");

        try
        {
            var inspect = _installer.Inspect([folder2], _fixtureRoot, _stagingRoot);
            Assert.True(inspect.Items[0].DestinationExists);
            Assert.Equal(InstallItemDecision.NeedsDecision, inspect.Items[0].Decision);

            var blocked = _installer.Execute(inspect.Items, _fixtureRoot);
            Assert.Equal(InstallItemOutcome.Rejected, blocked.Items[0].Outcome);
            Assert.Equal("original", File.ReadAllText(Path.Combine(_fixtureRoot, "chars", "DupHero", "marker.txt")));

            inspect.Items[0].Decision = InstallItemDecision.Replace;
            var replaced = _installer.Execute(inspect.Items, _fixtureRoot);
            Assert.Equal(1, replaced.InstalledCount);
            Assert.True(File.Exists(Path.Combine(_fixtureRoot, "chars", "DupHero", "new.txt")));
            Assert.False(File.Exists(Path.Combine(_fixtureRoot, "chars", "DupHero", "marker.txt")));

            var opId = replaced.Items[0].OperationId!;
            var rb = _mutations.Rollback(opId);
            Assert.True(rb.Success);
            Assert.True(File.Exists(Path.Combine(_fixtureRoot, "chars", "DupHero", "marker.txt")));
            Assert.Equal("original", File.ReadAllText(Path.Combine(_fixtureRoot, "chars", "DupHero", "marker.txt")));
            AssertSelectAndConfigUnchanged();
        }
        finally
        {
            TryDelete(parent1);
            TryDelete(parent2);
        }
    }

    [Fact]
    public void DuplicateStageDetected()
    {
        var parent = Path.Combine(Path.GetTempPath(), "dupstage-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(parent, "Ring");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Ring.def"), "[StageInfo]\nautoturn=1\n[BGdef]\nspr=Ring.sff\n");
        File.WriteAllBytes(Path.Combine(folder, "Ring.sff"), [1]);
        try
        {
            var first = _installer.Inspect([parent], _fixtureRoot, _stagingRoot);
            _installer.Execute(first.Items, _fixtureRoot);

            var second = _installer.Inspect([parent], _fixtureRoot, _stagingRoot);
            Assert.True(second.Items[0].DestinationExists);
            Assert.Equal(InstallItemDecision.NeedsDecision, second.Items[0].Decision);
        }
        finally
        {
            TryDelete(parent);
        }
    }

    [Fact]
    public void DryRunPreviewCausesZeroIkemenMutation()
    {
        var before = SnapshotTree();
        var zip = CreateCharacterZip("DryHero");
        var inspect = _installer.Inspect([zip], _fixtureRoot, _stagingRoot);
        var result = _installer.Execute(inspect.Items, _fixtureRoot, dryRun: true);
        Assert.Equal(1, result.InstalledCount);
        Assert.False(Directory.Exists(Path.Combine(_fixtureRoot, "chars", "DryHero")));
        Assert.Equal(before, SnapshotTree());
        AssertSelectAndConfigUnchanged();
        _installer.CleanupStaging(inspect.StagingDirectories);
        if (File.Exists(zip)) File.Delete(zip);
    }

    [Fact]
    public void BatchMixedSuccessAndFailure()
    {
        var parent = Path.Combine(Path.GetTempPath(), "batch-" + Guid.NewGuid().ToString("N"));
        var good = Path.Combine(parent, "BatchGood");
        Directory.CreateDirectory(good);
        WriteCharacterFiles(good, "BatchGood");
        var bad = Path.Combine(parent, "BatchBad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "x.txt"), "no");
        try
        {
            var inspect = _installer.Inspect([good, bad], _fixtureRoot, _stagingRoot);
            Assert.Single(inspect.Items);
            Assert.NotEmpty(inspect.Failures);
            var result = _installer.Execute(inspect.Items, _fixtureRoot);
            Assert.Equal(1, result.InstalledCount);
            Assert.True(Directory.Exists(Path.Combine(_fixtureRoot, "chars", "BatchGood")));
        }
        finally
        {
            TryDelete(parent);
        }
    }

    [Fact]
    public void LibraryRefreshSeesNewlyInstalledContent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "idx-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(parent, "IndexHero");
        Directory.CreateDirectory(folder);
        WriteCharacterFiles(folder, "IndexHero");
        try
        {
            var inspect = _installer.Inspect([folder], _fixtureRoot, _stagingRoot);
            _installer.Execute(inspect.Items, _fixtureRoot);

            var index = new LibraryIndexService().Index(_fixtureRoot);
            Assert.Contains(index.Characters, c =>
                c.DefPath.Contains("IndexHero", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(parent);
        }
    }

    [Fact]
    public void FailurePreservesOriginalContent()
    {
        var target = Path.Combine(_fixtureRoot, "chars", "KeepMe");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "KeepMe.def"), CharDef("KeepMe"));
        File.WriteAllText(Path.Combine(target, "keep.txt"), "safe");

        var result = _mutations.ReplaceDirectory(
            _fixtureRoot,
            target,
            Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N")));
        Assert.False(result.Success);
        Assert.Equal("safe", File.ReadAllText(Path.Combine(target, "keep.txt")));
        AssertSelectAndConfigUnchanged();
    }

    [Fact]
    public void NormalizeArchiveEntryRejectsTraversalAndAbsolute()
    {
        Assert.Throws<InvalidOperationException>(() => ArchiveExtractor.NormalizeArchiveEntryPath("../x"));
        Assert.Throws<InvalidOperationException>(() => ArchiveExtractor.NormalizeArchiveEntryPath("/etc/passwd"));
        Assert.Throws<InvalidOperationException>(() => ArchiveExtractor.NormalizeArchiveEntryPath(@"C:\Windows\x"));
        Assert.Equal(Path.Combine("a", "b.def"), ArchiveExtractor.NormalizeArchiveEntryPath("a/b.def"));
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

    private void AssertSelectAndConfigUnchanged()
    {
        Assert.Equal("; roster\n", File.ReadAllText(Path.Combine(_fixtureRoot, "data", "select.def")));
        Assert.Equal("[Config]\nMasterVolume = 50\n", File.ReadAllText(Path.Combine(_fixtureRoot, "save", "config.ini")));
    }

    private string CreateCharacterZip(string name)
    {
        var parent = Path.Combine(Path.GetTempPath(), "zipsrc-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(parent, name);
        Directory.CreateDirectory(folder);
        WriteCharacterFiles(folder, name);
        var zipPath = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N") + ".zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(parent, zipPath);
        TryDelete(parent);
        return zipPath;
    }

    private static string CreateCharacterFolder(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "char-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(folder);
        WriteCharacterFiles(folder, name);
        return folder;
    }

    private static void WriteCharacterFiles(string folder, string name)
    {
        File.WriteAllText(Path.Combine(folder, name + ".def"), CharDef(name));
        File.WriteAllText(Path.Combine(folder, "x.cmd"), ";cmd\n");
        File.WriteAllText(Path.Combine(folder, "x.cns"), ";cns\n");
        File.WriteAllText(Path.Combine(folder, "x.air"), ";air\n");
        File.WriteAllBytes(Path.Combine(folder, "x.sff"), [0]);
    }

    private static string CharDef(string name) =>
        $"[Info]\nname = {name}\nauthor = Test\n\n[Files]\nsprite = x.sff\nanim = x.air\ncmd = x.cmd\ncns = x.cns\n";

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
