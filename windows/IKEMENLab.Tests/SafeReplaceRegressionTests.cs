using System.Security.Cryptography;
using IKEMENLab.Core.Mutations;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Regression coverage for "Unable to remove the file to be replaced." (Win32 1175): ReplaceFile was
/// given a backup name under %LOCALAPPDATA% while the IKEMEN root was on another drive.
/// </summary>
public class SafeReplaceRegressionTests
{
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void CrossVolumeBackupWasTheRootCause()
    {
        // Documents the Windows behaviour behind the user-visible error (runs only with a second volume).
        using var f = MutationFixture.CrossVolume();
        if (f is null) return;

        var target = f.Write("data/select.def", "ORIGINAL");
        var staging = f.Write("data/.staging.tmp", "NEW");
        var crossVolumeBackup = Path.Combine(f.BackupDir, "replace-win.bak");

        var ex = Assert.ThrowsAny<IOException>(() => File.Replace(staging, target, crossVolumeBackup, true));
        Assert.Equal(unchecked((int)0x80070497), ex.HResult); // ERROR_UNABLE_TO_REMOVE_REPLACED
        Assert.Equal("ORIGINAL", File.ReadAllText(target));
    }

    [Fact]
    public void ReplaceSucceedsWhenRootIsOnAnotherVolume()
    {
        using var f = MutationFixture.CrossVolume();
        if (f is null) return;

        var target = f.Write("data/select.def", "[Characters]\nkfm\n");
        var before = Hash(target);
        var result = f.Mutations.ReplaceFile(f.Root, target, f.StageSource("[Characters]\nkfm\nberdly\n"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("[Characters]\nkfm\nberdly\n", File.ReadAllText(target));
        Assert.Equal("ReplaceFile", result.Manifest.ReplaceMethod);
        Assert.Equal(before, Hash(result.Manifest.BackupPath!));
        Assert.StartsWith(f.BackupDir, result.Manifest.BackupPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, ".ikemenlab-*"));
    }

    [Fact]
    public void OrdinaryReplaceKeepsVerifiedBackupAndLeavesNoStagingBehind()
    {
        using var f = MutationFixture.SameVolume();
        var target = f.Write("data/select.def", "one");
        var result = f.Mutations.ReplaceFile(f.Root, target, f.StageSource("two"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("two", File.ReadAllText(target));
        Assert.Equal("one", File.ReadAllText(result.Manifest.BackupPath!));
        Assert.Equal(result.Manifest.ExpectedAfterHash, result.Manifest.AfterHash);
        Assert.Equal(["select.def"], Directory.GetFiles(f.Full("data")).Select(Path.GetFileName));
    }

    [Fact]
    public void ReadOnlyTargetIsReplacedAndStaysReadOnly()
    {
        using var f = MutationFixture.SameVolume();
        var target = f.Write("save/config.ini", "[Video]\nVSync = 0\n");
        File.SetAttributes(target, FileAttributes.ReadOnly | FileAttributes.Archive);

        var result = f.Mutations.ReplaceFile(f.Root, target, f.StageSource("[Video]\nVSync = 1\n"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("[Video]\nVSync = 1\n", File.ReadAllText(target));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        Assert.True(result.Manifest.ReadOnlyPreserved);

        var rollback = f.Mutations.Rollback(result.OperationId);
        Assert.True(rollback.Success, rollback.Error);
        Assert.Equal("[Video]\nVSync = 0\n", File.ReadAllText(target));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void HiddenTargetKeepsItsAttributes()
    {
        using var f = MutationFixture.SameVolume();
        var target = f.Write("data/select.def", "a");
        File.SetAttributes(target, FileAttributes.Hidden | FileAttributes.Archive);

        var result = f.Mutations.ReplaceFile(f.Root, target, f.StageSource("b"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("b", File.ReadAllText(target));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void RepeatedReplacementsEachBackUpThePreviousVersion()
    {
        using var f = MutationFixture.SameVolume();
        var target = f.Write("data/select.def", "v0");
        for (var i = 1; i <= 5; i++)
        {
            var result = f.Mutations.ReplaceFile(f.Root, target, f.StageSource("v" + i));
            Assert.True(result.Success, result.Error);
            Assert.Equal("v" + (i - 1), File.ReadAllText(result.Manifest.BackupPath!));
            Assert.Equal("v" + i, File.ReadAllText(target));
        }
    }

    [Fact]
    public void LockedTargetFailsClearlyAndStaysByteIdentical()
    {
        using var f = MutationFixture.SameVolume();
        var target = f.Write("data/select.def", "[Characters]\nkfm\n");
        var before = File.ReadAllBytes(target);

        MutationResult result;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)) // no write/delete sharing
        {
            result = f.Mutations.ReplaceFile(f.Root, target, f.StageSource("changed"));
        }

        Assert.False(result.Success);
        Assert.Equal(MutationFailureKind.InUse, result.Manifest.FailureKind);
        Assert.Contains("in use", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not changed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unable to remove", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Manifest.Win32Error);
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(f.Full("data"), ".ikemenlab-*"));
        Assert.Equal(MutationStatus.Failed, f.Mutations.LoadManifest(result.OperationId)!.Status);
    }

    [Fact]
    public void ExternalChangeSinceReadIsAConflict()
    {
        using var f = MutationFixture.SameVolume();
        var target = f.Write("data/select.def", "read by app");
        var hashWhenRead = Hash(target);
        File.WriteAllText(target, "edited by VSelect");

        var result = f.Mutations.ReplaceFile(f.Root, target, f.StageSource("app edit"), expectedCurrentHash: hashWhenRead);

        Assert.False(result.Success);
        Assert.Equal(MutationFailureKind.Conflict, result.Manifest.FailureKind);
        Assert.Equal("edited by VSelect", File.ReadAllText(target));
    }

    [Fact]
    public void RollbackRestoresOriginalBytes()
    {
        using var f = MutationFixture.SameVolume();
        var target = f.WriteBytes("data/select.def", [0xEF, 0xBB, 0xBF, (byte)'x', (byte)'\r', (byte)'\n']);
        var before = File.ReadAllBytes(target);
        var result = f.Mutations.ReplaceFile(f.Root, target, f.StageSource("y"));
        Assert.True(result.Success, result.Error);

        var rollback = f.Mutations.Rollback(result.OperationId);
        Assert.True(rollback.Success, rollback.Error);
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void StaleStagingFromACrashIsSweptButFreshStagingIsKept()
    {
        using var f = MutationFixture.SameVolume();
        var stale = f.Write("data/.ikemenlab-old.tmp", "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
        var fresh = f.Write("data/.ikemenlab-new.tmp", "y");
        var target = f.Write("data/select.def", "a");

        Assert.True(f.Mutations.ReplaceFile(f.Root, target, f.StageSource("b")).Success);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void ReparsePointTargetIsRefused()
    {
        using var f = MutationFixture.SameVolume();
        var real = f.Write("data/real.def", "a");
        var link = f.Full("data/select.def");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // symlink creation needs Developer Mode or elevation
        }

        var result = f.Mutations.ReplaceFile(f.Root, link, f.StageSource("b"));
        Assert.False(result.Success);
        Assert.Equal("a", File.ReadAllText(real));
    }
}
