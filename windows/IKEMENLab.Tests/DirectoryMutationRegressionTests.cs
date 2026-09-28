using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Install/replace of whole character and stage folders. The folder is staged beside the target
/// and swapped in by rename, so it is never half-written, the app keeps exactly one verified backup,
/// and no staging copy is left behind (runtime QA found a full duplicate of every install in app data).
/// </summary>
public class DirectoryMutationRegressionTests
{
    public static IEnumerable<object[]> Volumes => [["same"], ["cross"]];

    private static MutationFixture? Open(string volume)
        => volume == "same" ? MutationFixture.SameVolume() : MutationFixture.CrossVolume();

    private static string MakePackage(MutationFixture f, string name, params (string Rel, string Content)[] files)
    {
        var dir = Path.Combine(f.StagingDir, "pkg-" + Guid.NewGuid().ToString("N"), name);
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return dir;
    }

    private static string?[] Leftovers(MutationFixture f, string parent)
        => Directory.GetFileSystemEntries(f.Full(parent), ".ikemenlab-*").Select(Path.GetFileName).ToArray();

    [Theory]
    [MemberData(nameof(Volumes))]
    public void CreateDirectoryAppearsCompleteAndKeepsNoDuplicateInAppData(string volume)
    {
        using var f = Open(volume);
        if (f is null) return;
        var pkg = MakePackage(f, "berdly", ("berdly.def", "[Info]\nname = Berdly\n"), ("anim/berdly.air", "anim"));
        var target = f.Full("chars/berdly");

        var result = f.Mutations.CreateDirectory(f.Root, target, pkg);

        Assert.True(result.Success, result.Error);
        Assert.Equal("anim", File.ReadAllText(Path.Combine(target, "anim", "berdly.air")));
        Assert.Equal(result.Manifest.ExpectedAfterHash, result.Manifest.AfterHash);
        Assert.Empty(Leftovers(f, "chars"));
        Assert.Empty(Directory.GetFiles(result.Manifest.BackupPath!, "*.def", SearchOption.AllDirectories));

        var rollback = f.Mutations.Rollback(result.OperationId);
        Assert.True(rollback.Success, rollback.Error);
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public void ReplaceDirectorySwapsByRenameWithOneVerifiedBackup(string volume)
    {
        using var f = Open(volume);
        if (f is null) return;
        f.Write("chars/berdly/berdly.def", "OLD");
        f.Write("chars/berdly/old-only.txt", "previous version");
        File.SetAttributes(f.Full("chars/berdly/berdly.def"), FileAttributes.ReadOnly);
        var pkg = MakePackage(f, "berdly", ("berdly.def", "NEW"));
        var target = f.Full("chars/berdly");

        var result = f.Mutations.ReplaceDirectory(f.Root, target, pkg);

        Assert.True(result.Success, result.Error);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(target, "berdly.def")));
        Assert.False(File.Exists(Path.Combine(target, "old-only.txt")));
        Assert.Equal(result.Manifest.ExpectedAfterHash, result.Manifest.AfterHash);
        Assert.Null(result.Manifest.ErrorDetail);
        Assert.Empty(Leftovers(f, "chars"));

        var operationBackup = Path.GetDirectoryName(result.Manifest.BackupPath!)!;
        Assert.Equal(["original"], Directory.GetDirectories(operationBackup).Select(Path.GetFileName));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(result.Manifest.BackupPath!, "berdly.def")));

        var rollback = f.Mutations.Rollback(result.OperationId);
        Assert.True(rollback.Success, rollback.Error);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(target, "berdly.def")));
        Assert.Equal("previous version", File.ReadAllText(Path.Combine(target, "old-only.txt")));
        Assert.Empty(Leftovers(f, "chars"));
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public void OpenFileInsideTargetFailsClearlyAndLeavesFolderUntouched(string volume)
    {
        using var f = Open(volume);
        if (f is null) return;
        f.Write("chars/berdly/berdly.def", "OLD");
        f.Write("chars/berdly/berdly.sff", "SPRITES");
        var target = f.Full("chars/berdly");
        var before = IkemenPathGuard.Sha256DirectoryFingerprint(target);
        var pkg = MakePackage(f, "berdly", ("berdly.def", "NEW"));

        MutationResult result;
        using (new FileStream(Path.Combine(target, "berdly.sff"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = f.Mutations.ReplaceDirectory(f.Root, target, pkg);
        }

        Assert.False(result.Success);
        Assert.Equal(MutationFailureKind.InUse, result.Manifest.FailureKind);
        Assert.Contains("'berdly' (or a file inside it)", result.Error);
        Assert.EndsWith(FileReplacer.FolderUnchanged, result.Error);
        Assert.Equal(before, IkemenPathGuard.Sha256DirectoryFingerprint(target));
        Assert.Empty(Leftovers(f, "chars"));
    }

    [Fact]
    public void CreateDirectoryNeverMergesIntoAnExistingFolder()
    {
        using var f = MutationFixture.SameVolume();
        f.Write("chars/kfm/kfm.def", "MINE");
        var pkg = MakePackage(f, "kfm", ("kfm.def", "THEIRS"), ("extra.txt", "x"));

        var result = f.Mutations.CreateDirectory(f.Root, f.Full("chars/kfm"), pkg);

        Assert.False(result.Success);
        Assert.Equal("MINE", File.ReadAllText(f.Full("chars/kfm/kfm.def")));
        Assert.False(File.Exists(f.Full("chars/kfm/extra.txt")));
        Assert.Empty(Leftovers(f, "chars"));
    }

    [Fact]
    public void AbandonedStagingFoldersAreSweptButInFlightOnesAreKept()
    {
        using var f = MutationFixture.SameVolume();
        var stale = f.Full("chars/.ikemenlab-20200101T000000-dead.tmp");
        f.Write("chars/.ikemenlab-20200101T000000-dead.tmp/x.def", "partial");
        File.SetAttributes(Path.Combine(stale, "x.def"), FileAttributes.ReadOnly);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
        var inFlight = f.Full("chars/.ikemenlab-inflight.tmp");
        Directory.CreateDirectory(inFlight);

        var result = f.Mutations.CreateDirectory(f.Root, f.Full("chars/berdly"), MakePackage(f, "berdly", ("berdly.def", "NEW")));

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(inFlight));
    }

    [Fact]
    public void LibraryScanIgnoresStagingFolders()
    {
        using var f = MutationFixture.SameVolume();
        f.WriteChar("kfm");
        f.WriteChar(".ikemenlab-op.tmp", "kfm");
        const string stage = "[StageInfo]\nname = Arena\n\n[BGdef]\nspr = a.sff\n";
        f.Write("stages/Arena/arena.def", stage);
        f.Write("stages/.ikemenlab-op.tmp/arena.def", stage);

        var characters = new CharacterIndexer().Index(f.Root, out _);
        var stages = new StageIndexer().Index(f.Root, out _);

        Assert.Equal(["kfm"], characters.Select(c => c.Id));
        Assert.Equal(["Arena/arena.def"], stages.Select(s => s.Id));
    }
}
