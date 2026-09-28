using IKEMENLab.Core.Mutations;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// SafeMutation DeleteDirectory: verified backup first, then one atomic rename out of the installation,
/// so a folder is either fully present or gone — and Rollback puts it back exactly.
/// </summary>
public sealed class DeleteDirectoryMutationTests : IDisposable
{
    private readonly MutationFixture _f = MutationFixture.SameVolume();

    public void Dispose() => _f.Dispose();

    private string SeedGaara()
    {
        _f.Write("chars/Gaara/Gaara.def", "[Info]\nname = Gaara\n");
        _f.Write("chars/Gaara/sprites/Gaara.sff", "sff-bytes");
        _f.Write("chars/Gaara/sound/Gaara.snd", "snd-bytes");
        var readOnly = _f.Write("chars/Gaara/readme.txt", "read me");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        return _f.Full("chars/Gaara");
    }

    private string[] Leftovers() =>
        Directory.GetFileSystemEntries(_f.Full("chars"), IkemenLabStaging.Prefix + "*").Select(Path.GetFileName).ToArray()!;

    [Fact]
    public void DeleteRemovesTheFolderKeepsAVerifiedBackupAndRollbackRestoresIt()
    {
        var target = SeedGaara();
        _f.Write("chars/Naruto/Naruto.def", "[Info]\nname = Naruto\n");
        var before = IkemenPathGuard.Sha256DirectoryFingerprint(target);

        var result = _f.Mutations.DeleteDirectory(_f.Root, target);

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(target));
        Assert.True(File.Exists(_f.Full("chars/Naruto/Naruto.def")));
        Assert.Empty(Leftovers());
        Assert.Null(result.Manifest.ErrorDetail);
        Assert.Equal(MutationKind.DeleteDirectory, _f.Mutations.LoadManifest(result.OperationId)!.Kind);
        Assert.Equal(before, result.Manifest.BeforeHash);
        Assert.Equal(before, IkemenPathGuard.Sha256DirectoryFingerprint(result.Manifest.BackupPath!));

        var rollback = _f.Mutations.Rollback(result.OperationId);

        Assert.True(rollback.Success, rollback.Error);
        Assert.Equal(before, IkemenPathGuard.Sha256DirectoryFingerprint(target));
        Assert.Equal(MutationStatus.RolledBack, _f.Mutations.LoadManifest(result.OperationId)!.Status);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void DryRunChangesNothing()
    {
        var target = SeedGaara();
        var before = IkemenPathGuard.Sha256DirectoryFingerprint(target);

        var result = _f.Mutations.DeleteDirectory(_f.Root, target, dryRun: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal(before, IkemenPathGuard.Sha256DirectoryFingerprint(target));
        Assert.Null(_f.Mutations.LoadManifest(result.OperationId));
    }

    [Fact]
    public void PlanRejectsMissingFolderFileTargetRootAndEscapes()
    {
        _f.Write("chars/notes.txt", "x");

        Assert.False(_f.Mutations.PlanDeleteDirectory(_f.Root, _f.Full("chars/Nobody")).IsValid);
        Assert.False(_f.Mutations.PlanDeleteDirectory(_f.Root, _f.Full("chars/notes.txt")).IsValid);
        Assert.False(_f.Mutations.PlanDeleteDirectory(_f.Root, _f.Root).IsValid);
        Assert.False(_f.Mutations.PlanDeleteDirectory(_f.Root, "chars/../../elsewhere").IsValid);
        Assert.False(_f.Mutations.PlanDeleteDirectory(_f.Root, _f.AppDir).IsValid);

        var result = _f.Mutations.DeleteDirectory(_f.Root, _f.Full("chars/Nobody"));
        Assert.False(result.Success);
        Assert.True(File.Exists(_f.Full("chars/notes.txt")));
    }

    [Fact]
    public void RollbackNeverOverwritesAFolderThatReappearedWithOtherContent()
    {
        var target = SeedGaara();
        var result = _f.Mutations.DeleteDirectory(_f.Root, target);
        Assert.True(result.Success, result.Error);
        _f.Write("chars/Gaara/Gaara.def", "[Info]\nname = New Gaara\n");

        var rollback = _f.Mutations.Rollback(result.OperationId);

        Assert.False(rollback.Success);
        Assert.Contains("was not restored over it", rollback.Error);
        Assert.Equal("[Info]\nname = New Gaara\n", File.ReadAllText(_f.Full("chars/Gaara/Gaara.def")));
    }

    [Fact]
    public void LinkedFolderIsNotDeleted()
    {
        var outside = Path.Combine(_f.AppDir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var link = _f.Full("chars/Linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // creating links needs privileges on some Windows setups
        }

        var result = _f.Mutations.DeleteDirectory(_f.Root, link);

        Assert.False(result.Success);
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void OpenFileInsideFailsClearlyAndLeavesTheFolderUntouched()
    {
        // Only Windows refuses to rename a folder that contains an open file.
        if (!OperatingSystem.IsWindows()) return;

        var target = SeedGaara();
        var before = IkemenPathGuard.Sha256DirectoryFingerprint(target);
        MutationResult result;
        using (new FileStream(Path.Combine(target, "sprites", "Gaara.sff"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = _f.Mutations.DeleteDirectory(_f.Root, target);
        }

        Assert.False(result.Success);
        Assert.Equal(MutationFailureKind.InUse, result.Manifest.FailureKind);
        Assert.Contains(FileReplacer.FolderUnchanged, result.Error);
        Assert.Equal(before, IkemenPathGuard.Sha256DirectoryFingerprint(target));
        Assert.Empty(Leftovers());
    }
}
