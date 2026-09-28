using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using IKEMENLab.Core.Mutations;
using Xunit;

namespace IKEMENLab.Tests;

public class SafeMutationServiceTests : IDisposable
{
    private readonly string _fixtureRoot;
    private readonly string _opsRoot;
    private readonly string _backupRoot;
    private readonly SafeMutationService _svc;

    public SafeMutationServiceTests()
    {
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-safe-" + Guid.NewGuid().ToString("N"));
        _opsRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-ops-" + Guid.NewGuid().ToString("N"));
        _backupRoot = Path.Combine(Path.GetTempPath(), "ikemenlab-bak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "chars"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "stages"));
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "data"));
        Directory.CreateDirectory(_opsRoot);
        Directory.CreateDirectory(_backupRoot);
        _svc = new SafeMutationService(_opsRoot, _backupRoot);
    }

    public void Dispose()
    {
        TryDelete(_fixtureRoot);
        TryDelete(_opsRoot);
        TryDelete(_backupRoot);
    }

    [Fact]
    public void CreateNewFile()
    {
        var target = Path.Combine(_fixtureRoot, "data", "hello.txt");
        var result = _svc.CreateFile(_fixtureRoot, target, Encoding.UTF8.GetBytes("hi"));
        Assert.True(result.Success);
        Assert.Equal("hi", File.ReadAllText(target));
        Assert.Equal(MutationStatus.Succeeded, result.Manifest.Status);
        Assert.False(string.IsNullOrEmpty(result.Manifest.AfterHash));
        Assert.True(File.Exists(Path.Combine(_opsRoot, result.OperationId, "manifest.json")));
    }

    [Fact]
    public void ReplaceFileCreatesBackupAndChangesContent()
    {
        var target = Path.Combine(_fixtureRoot, "data", "select.def");
        File.WriteAllText(target, "before");
        var source = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N") + ".def");
        File.WriteAllText(source, "after");
        try
        {
            var result = _svc.ReplaceFile(_fixtureRoot, target, source);
            Assert.True(result.Success);
            Assert.Equal("after", File.ReadAllText(target));
            Assert.False(string.IsNullOrEmpty(result.Manifest.BeforeHash));
            Assert.False(string.IsNullOrEmpty(result.Manifest.AfterHash));
            Assert.NotEqual(result.Manifest.BeforeHash, result.Manifest.AfterHash);
            Assert.True(File.Exists(result.Manifest.BackupPath));
            Assert.Equal("before", File.ReadAllText(result.Manifest.BackupPath!));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void SuccessfulRollbackRestoresReplacedFile()
    {
        var target = Path.Combine(_fixtureRoot, "data", "cfg.ini");
        File.WriteAllText(target, "v1");
        var source = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(source, "v2");
        try
        {
            var result = _svc.ReplaceFile(_fixtureRoot, target, source);
            Assert.True(result.Success);
            var rb = _svc.Rollback(result.OperationId);
            Assert.True(rb.Success);
            Assert.Equal("v1", File.ReadAllText(target));
            Assert.Equal(MutationStatus.RolledBack, rb.Manifest.Status);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void FailedReplacementRollsBackPreservingDestination()
    {
        var target = Path.Combine(_fixtureRoot, "data", "keep.txt");
        File.WriteAllText(target, "original");
        // Source missing => plan invalid / execute fails without clobbering.
        var missing = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".txt");
        var result = _svc.ReplaceFile(_fixtureRoot, target, missing);
        Assert.False(result.Success);
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public void NewDirectoryInstall()
    {
        var source = Path.Combine(Path.GetTempPath(), "chardir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "char.def"), "[Info]\nname=X\n");
        try
        {
            var target = Path.Combine(_fixtureRoot, "chars", "NewChar");
            var result = _svc.CreateDirectory(_fixtureRoot, target, source);
            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(target, "char.def")));
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public void DirectoryReplacementAndRollback()
    {
        var target = Path.Combine(_fixtureRoot, "chars", "OldPack");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "old");

        var source = Path.Combine(Path.GetTempPath(), "newpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "b.txt"), "new");
        try
        {
            var result = _svc.ReplaceDirectory(_fixtureRoot, target, source);
            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(target, "b.txt")));
            Assert.False(File.Exists(Path.Combine(target, "a.txt")));

            var rb = _svc.Rollback(result.OperationId);
            Assert.True(rb.Success);
            Assert.True(File.Exists(Path.Combine(target, "a.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "a.txt")));
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public void PathTraversalRejected()
    {
        var plan = _svc.PlanCreateFile(_fixtureRoot, @"..\escape.txt", "x"u8.ToArray());
        Assert.False(plan.IsValid);
        Assert.Contains("traversal", plan.RejectionReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetOutsideRootRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var plan = _svc.PlanCreateFile(_fixtureRoot, outside, "x"u8.ToArray());
        Assert.False(plan.IsValid);
        Assert.Contains("escape", plan.RejectionReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReparseJunctionEscapeRejectedWhenPractical()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var outside = Path.Combine(Path.GetTempPath(), "outside-junc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "nope");
        var junction = Path.Combine(_fixtureRoot, "chars", "EscapeGate");
        try
        {
            if (!TryCreateJunction(junction, outside))
            {
                return; // environment cannot create junctions — skip without failing the suite
            }

            var targetThroughJunction = Path.Combine(junction, "planted.txt");
            var plan = _svc.PlanCreateFile(_fixtureRoot, targetThroughJunction, "bad"u8.ToArray());
            Assert.False(plan.IsValid);
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                // Junction removal must not delete outside content.
                Directory.Delete(junction);
            }

            TryDelete(outside);
        }
    }

    [Fact]
    public void PreExistingDestinationPreservedOnFailure()
    {
        var target = Path.Combine(_fixtureRoot, "stages", "keep.def");
        File.WriteAllText(target, "stage");
        var result = _svc.CreateFile(_fixtureRoot, target, "nope"u8.ToArray());
        Assert.False(result.Success);
        Assert.Equal("stage", File.ReadAllText(target));
    }

    [Fact]
    public void ManifestWrittenWithHashes()
    {
        var target = Path.Combine(_fixtureRoot, "data", "hashme.txt");
        File.WriteAllText(target, "A");
        var source = Path.Combine(Path.GetTempPath(), "hashsrc-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(source, "B");
        try
        {
            var result = _svc.ReplaceFile(_fixtureRoot, target, source);
            var loaded = _svc.LoadManifest(result.OperationId);
            Assert.NotNull(loaded);
            Assert.Equal(result.Manifest.BeforeHash, loaded!.BeforeHash);
            Assert.Equal(result.Manifest.AfterHash, loaded.AfterHash);
            Assert.Equal(MutationStatus.Succeeded, loaded.Status);
            Assert.Equal(MutationKind.ReplaceFile, loaded.Kind);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void MultipleOperationsDoNotCollide()
    {
        var a = _svc.CreateFile(_fixtureRoot, Path.Combine(_fixtureRoot, "data", "a.txt"), "a"u8.ToArray());
        var b = _svc.CreateFile(_fixtureRoot, Path.Combine(_fixtureRoot, "data", "b.txt"), "b"u8.ToArray());
        Assert.True(a.Success);
        Assert.True(b.Success);
        Assert.NotEqual(a.OperationId, b.OperationId);
        Assert.True(Directory.Exists(Path.Combine(_opsRoot, a.OperationId)));
        Assert.True(Directory.Exists(Path.Combine(_opsRoot, b.OperationId)));
        Assert.True(Directory.Exists(Path.Combine(_backupRoot, a.OperationId)));
        Assert.True(Directory.Exists(Path.Combine(_backupRoot, b.OperationId)));
    }

    [Fact]
    public void DryRunPerformsZeroMutation()
    {
        var before = Snapshot(_fixtureRoot);
        var target = Path.Combine(_fixtureRoot, "data", "dry.txt");
        var result = _svc.CreateFile(_fixtureRoot, target, "x"u8.ToArray(), dryRun: true);
        Assert.True(result.Success);
        Assert.True(result.Manifest.DryRun);
        Assert.False(File.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(_opsRoot, result.OperationId)));
        AssertEqualSnapshots(before, Snapshot(_fixtureRoot));
    }

    [Fact]
    public void RealInstallStyleFixtureUnchangedUnlessExecute()
    {
        // Simulate a real install layout; plan only.
        File.WriteAllBytes(Path.Combine(_fixtureRoot, "Ikemen_GO.exe"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(_fixtureRoot, "data", "select.def"), "; roster\n");
        var before = Snapshot(_fixtureRoot);

        var plan = _svc.PlanReplaceFile(
            _fixtureRoot,
            Path.Combine(_fixtureRoot, "data", "select.def"),
            Path.Combine(_fixtureRoot, "data", "select.def"));
        // source==target rejected
        Assert.False(plan.IsValid);

        var dry = _svc.CreateFile(
            _fixtureRoot,
            Path.Combine(_fixtureRoot, "chars", "Injected", "x.def"),
            "[Info]\n"u8.ToArray(),
            dryRun: true);
        Assert.True(dry.Success);
        AssertEqualSnapshots(before, Snapshot(_fixtureRoot));
    }

    [Fact]
    public void RollbackRemovesCreatedFile()
    {
        var target = Path.Combine(_fixtureRoot, "data", "temp-create.txt");
        var result = _svc.CreateFile(_fixtureRoot, target, "z"u8.ToArray());
        Assert.True(File.Exists(target));
        var rb = _svc.Rollback(result.OperationId);
        Assert.True(rb.Success);
        Assert.False(File.Exists(target));
    }

    private static Dictionary<string, (long len, string hash)> Snapshot(string root)
    {
        var map = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var bytes = File.ReadAllBytes(file);
            map[rel] = (bytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        }

        return map;
    }

    private static void AssertEqualSnapshots(
        Dictionary<string, (long len, string hash)> before,
        Dictionary<string, (long len, string hash)> after)
    {
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var key in before.Keys)
        {
            Assert.Equal(before[key], after[key]);
        }
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            // Prefer cmd mklink /J — does not require admin.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(junctionPath);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort cleanup
        }
    }
}
