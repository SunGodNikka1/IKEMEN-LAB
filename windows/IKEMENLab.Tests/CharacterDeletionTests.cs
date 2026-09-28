using System.Text;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Delete Character removes the whole package folder from chars/ and every select.def line that belongs
/// to it — completely, so the next character moves up into the freed slot — through SafeMutation, and
/// never leaves select.def and the folder out of step. Disposable fixture roots only.
/// </summary>
public sealed class CharacterDeletionTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly MutationFixture _f = MutationFixture.SameVolume();
    private readonly DateAddedTracker _tracker;
    private readonly PrimaryDefStore _primaryDefs;

    public CharacterDeletionTests()
    {
        _tracker = new DateAddedTracker(new DateAddedStore(Path.Combine(_f.AppDir, "date-added")));
        _primaryDefs = new PrimaryDefStore(Path.Combine(_f.AppDir, "primary-def"));
    }

    public void Dispose() => _f.Dispose();

    private string SelectPath => _f.Full("data/select.def");

    private CharacterDeletionService Service(ISafeMutationService? mutations = null)
        => new(mutations ?? _f.Mutations, _tracker, _primaryDefs, Path.Combine(_f.AppDir, "roster-staging"));

    private void Char(string folder, string? def = null)
        => PrimaryDefResolverTests.WriteDef(_f.Full("chars/" + folder), (def ?? folder) + ".def", def ?? folder);

    private void Roster(params string[] folders)
    {
        foreach (var folder in folders) Char(folder);
    }

    private byte[] WriteSelect(string text, bool bom = false)
    {
        byte[] bytes = bom ? [.. Bom, .. Encoding.UTF8.GetBytes(text)] : Encoding.UTF8.GetBytes(text);
        _f.WriteBytes("data/select.def", bytes);
        return bytes;
    }

    private string ReadSelect() => File.ReadAllText(SelectPath);

    private LibrarySnapshot Index() => new LibraryIndexService(_tracker, _primaryDefs).Index(_f.Root);

    /// <summary>[Characters] entries in select-screen order, as IKEMEN reads them.</summary>
    private IReadOnlyList<string> CharacterOrder()
        => SelectDefReader.Parse(ReadSelect())
            .Where(e => e.Section == SelectDefSection.Characters)
            .Select(e => (e.IsCommented ? ";" : "") + e.RawName)
            .ToList();

    private string[] Leftovers(string parent)
        => Directory.GetFileSystemEntries(_f.Full(parent), IkemenLabStaging.Prefix + "*").Select(Path.GetFileName).ToArray()!;

    [Fact]
    public void DeletingActiveMiddleCharacterRemovesItsLineAndTheNextCharacterMovesUp()
    {
        Roster("Naruto", "Gaara", "Muzan", "CHASE");
        _f.Write("stages/leaf.def", "[Info]\nname = Leaf\n");
        const string original =
            "; my roster\r\n" +
            "[Characters]\r\n" +
            "Naruto, stages/leaf.def\r\n" +
            "Gaara, order=2 ; sand\r\n" +
            "Muzan\r\n" +
            "CHASE\r\n" +
            "\r\n" +
            "[ExtraStages]\r\n" +
            "stages/leaf.def\r\n" +
            "\r\n" +
            "[Options]\r\n" +
            "arcade.maxmatches = 6,1\r\n";
        WriteSelect(original, bom: true);
        Assert.Equal(["Naruto", "Gaara", "Muzan", "CHASE"], CharacterOrder());

        var result = Service().Delete(_f.Root, "Gaara");

        Assert.True(result.Success, result.Error);
        var removed = Assert.Single(result.RemovedRosterEntries);
        Assert.Equal(4, removed.LineNumber);
        Assert.Equal("Gaara, order=2 ; sand", removed.Text);

        // Byte-for-byte: the BOM, CRLF endings, comments, stages and options are untouched; the line is
        // gone entirely (no ';' placeholder, no blank slot).
        var expected = original.Replace("Gaara, order=2 ; sand\r\n", string.Empty);
        Assert.Equal([.. Bom, .. Encoding.UTF8.GetBytes(expected)], File.ReadAllBytes(SelectPath));
        Assert.Equal(["Naruto", "Muzan", "CHASE"], CharacterOrder());

        Assert.False(Directory.Exists(_f.Full("chars/Gaara")));
        foreach (var other in new[] { "Naruto", "Muzan", "CHASE" })
            Assert.True(File.Exists(_f.Full($"chars/{other}/{other}.def")));
        Assert.Empty(Leftovers("chars"));

        var snapshot = Index();
        Assert.DoesNotContain(snapshot.Characters, c => c.Id == "Gaara");
        Assert.Equal(3, snapshot.CharacterCount);
        Assert.All(snapshot.Characters, c => Assert.Equal(ContentStatus.Active, c.Status));
    }

    [Fact]
    public void DeletingFirstRosterCharacterShiftsEveryoneUp()
    {
        Roster("Naruto", "Gaara", "Muzan", "CHASE");
        const string original = "[Characters]\nNaruto\nGaara\nMuzan\nCHASE\n\n[ExtraStages]\n";
        WriteSelect(original);

        var result = Service().Delete(_f.Root, "Naruto");

        Assert.True(result.Success, result.Error);
        Assert.Equal("[Characters]\nGaara\nMuzan\nCHASE\n\n[ExtraStages]\n", ReadSelect());
        Assert.Equal(["Gaara", "Muzan", "CHASE"], CharacterOrder());
        Assert.False(Directory.Exists(_f.Full("chars/Naruto")));
    }

    [Fact]
    public void DeletingLastRosterCharacterKeepsTheFollowingSections()
    {
        Roster("Naruto", "Gaara", "Muzan", "CHASE");
        const string original = "[Characters]\nNaruto\nGaara\nMuzan\nCHASE\n[ExtraStages]\nstages/a.def\n";
        WriteSelect(original);

        var result = Service().Delete(_f.Root, "CHASE");

        Assert.True(result.Success, result.Error);
        Assert.Equal("[Characters]\nNaruto\nGaara\nMuzan\n[ExtraStages]\nstages/a.def\n", ReadSelect());
        Assert.False(Directory.Exists(_f.Full("chars/CHASE")));
    }

    [Fact]
    public void DeletingTheFinalLineOfAFileWithoutTrailingNewlineKeepsItWithoutOne()
    {
        Roster("Naruto", "Gaara", "Muzan", "CHASE");
        WriteSelect("[Characters]\r\nNaruto\r\nGaara\r\nMuzan\r\nCHASE");

        var result = Service().Delete(_f.Root, "CHASE");

        Assert.True(result.Success, result.Error);
        Assert.Equal("[Characters]\r\nNaruto\r\nGaara\r\nMuzan", ReadSelect());
    }

    [Fact]
    public void DeletingADisabledCharacterRemovesTheCommentedEntryButNotUnrelatedComments()
    {
        Roster("Naruto", "Gaara", "Muzan");
        const string original =
            "[Characters]\n" +
            "; Gaara is my favourite\n" +
            "Naruto\n" +
            ";Gaara, stages/sand.def\n" +
            "Muzan\n";
        WriteSelect(original);
        Assert.Equal(ContentStatus.Disabled, Index().Characters.Single(c => c.Id == "Gaara").Status);

        var result = Service().Delete(_f.Root, "Gaara");

        Assert.True(result.Success, result.Error);
        var removed = Assert.Single(result.RemovedRosterEntries);
        Assert.True(removed.IsCommented);
        Assert.Equal("[Characters]\n; Gaara is my favourite\nNaruto\nMuzan\n", ReadSelect());
        Assert.False(Directory.Exists(_f.Full("chars/Gaara")));
    }

    [Fact]
    public void DeletingAMultiDefCharacterRemovesEveryVariantReferenceAndKeepsSimilarNames()
    {
        var muzan = _f.Full("chars/Muzan");
        PrimaryDefResolverTests.WriteDef(muzan, "Muzan.def", "Muzan");
        PrimaryDefResolverTests.WriteDef(muzan, "Muzan_AI.def", "Muzan");
        PrimaryDefResolverTests.WriteDef(muzan, "Muzan_noAI.def", "Muzan");
        PrimaryDefResolverTests.WriteKfmPlaceholder(muzan);
        Roster("Naruto", "MuzanX", "CHASE", "muzan_old");
        const string original =
            "[Characters]\n" +
            "Naruto\n" +
            "Muzan/Muzan_AI.def, order=1\n" +
            "MuzanX\n" +
            ";Muzan/Muzan_noAI.def\n" +
            "chars/Muzan/Muzan.def\n" +
            "Muzan\\KFM.def\n" +
            ";Muzan/Muzan_v1.def ; DEF no longer exists\n" +
            "CHASE\n" +
            ";muzan_old\n" +
            "[ExtraStages]\n";
        WriteSelect(original);

        var before = Index().Characters.Single(c => c.Id == "Muzan");
        Assert.Equal(ContentStatus.Active, before.Status);
        Assert.True(before.ActiveDefPaths.Count >= 2);

        var plan = Service().Preview(_f.Root, before);
        Assert.True(plan.CanDelete, plan.Error);
        Assert.Equal([3, 5, 6, 7, 8], plan.RosterEntries.Select(r => r.LineNumber));

        var result = Service().Delete(_f.Root, before);

        Assert.True(result.Success, result.Error);
        Assert.Equal(5, result.RemovedRosterEntries.Count);
        Assert.Equal("[Characters]\nNaruto\nMuzanX\nCHASE\n;muzan_old\n[ExtraStages]\n", ReadSelect());
        Assert.False(Directory.Exists(muzan));
        Assert.True(Directory.Exists(_f.Full("chars/MuzanX")));
        Assert.True(Directory.Exists(_f.Full("chars/muzan_old")));

        var after = Index();
        Assert.DoesNotContain(after.Characters, c => c.Id.Equals("Muzan", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ContentStatus.Active, after.Characters.Single(c => c.Id == "MuzanX").Status);
        Assert.Empty(after.SelectDef!.MissingEntries);
    }

    [Fact]
    public void DeletingANestedCharacterRemovesItsWholeTopLevelPackage()
    {
        PrimaryDefResolverTests.WriteDef(_f.Full("chars/Pack/Ken"), "Ken.def", "Ken");
        Roster("Ken", "Ryu");
        const string original = "[Characters]\nRyu\nPack/Ken/Ken.def, order=3\nKen\n;Pack/Ken/Ken_old.def\n";
        WriteSelect(original);

        var nested = Index().Characters.Single(c => c.Id.StartsWith("Pack", StringComparison.OrdinalIgnoreCase));
        Assert.True(nested.Nested);
        Assert.Equal(ContentStatus.Active, nested.Status);

        var result = Service().Delete(_f.Root, nested);

        Assert.True(result.Success, result.Error);
        Assert.Equal("chars/Pack", result.Plan.PackageFolder);
        Assert.Equal("[Characters]\nRyu\nKen\n", ReadSelect());
        Assert.False(Directory.Exists(_f.Full("chars/Pack")));
        Assert.True(File.Exists(_f.Full("chars/Ken/Ken.def")), "the separate top-level Ken package must stay");
        Assert.Equal(["Ken", "Ryu"], Index().Characters.Select(c => c.Id).Order());
    }

    [Fact]
    public void DeletingAnUnregisteredCharacterLeavesSelectDefByteIdentical()
    {
        Roster("Naruto", "Loner");
        var bytes = WriteSelect("[Characters]\r\nNaruto\r\n", bom: true);

        var result = Service().Delete(_f.Root, "Loner");

        Assert.True(result.Success, result.Error);
        Assert.Null(result.SelectDefOperationId);
        Assert.Empty(result.RemovedRosterEntries);
        Assert.Equal(bytes, File.ReadAllBytes(SelectPath));
        Assert.False(Directory.Exists(_f.Full("chars/Loner")));
    }

    [Fact]
    public void SuccessfulDeleteRetiresOnlyThatPackagesDateAddedAndPrimaryDefRecords()
    {
        Roster("Naruto", "Gaara");
        WriteSelect("[Characters]\nNaruto\nGaara\n");
        Index(); // establishes Date Added records for both
        _primaryDefs.Set(_f.Root, "Gaara", "Gaara.def");
        _primaryDefs.Set(_f.Root, "Naruto", "Naruto.def");
        var store = new DateAddedStore(Path.Combine(_f.AppDir, "date-added"));
        Assert.True(store.Read(_f.Root).Items.ContainsKey(ContentIdentity.ForCharacterFolder("Gaara")));

        var result = Service().Delete(_f.Root, "Gaara");

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Warnings);
        var items = store.Read(_f.Root).Items;
        Assert.False(items.ContainsKey(ContentIdentity.ForCharacterFolder("Gaara")));
        Assert.True(items.ContainsKey(ContentIdentity.ForCharacterFolder("Naruto")));
        Assert.Null(_primaryDefs.Get(_f.Root, "Gaara"));
        Assert.Equal("Naruto.def", _primaryDefs.Get(_f.Root, "Naruto"));
    }

    [Fact]
    public void FolderDeleteFailureRollsSelectDefBackAndKeepsMetadata()
    {
        Roster("Naruto", "Gaara", "Muzan");
        var bytes = WriteSelect("[Characters]\r\nNaruto\r\nGaara\r\nMuzan\r\n", bom: true);
        Index();
        _primaryDefs.Set(_f.Root, "Gaara", "Gaara.def");
        var faulty = new FaultyMutations(_f.Mutations) { FailDeleteDirectory = true };

        var result = Service(faulty).Delete(_f.Root, "Gaara");

        Assert.False(result.Success);
        Assert.True(result.SelectDefRestored);
        Assert.NotNull(result.SelectDefOperationId);
        Assert.Contains("chars/Gaara", result.Error);
        Assert.Contains(FaultyMutations.InjectedError, result.Error);
        Assert.Contains("select.def was restored", result.Error);
        Assert.Equal(bytes, File.ReadAllBytes(SelectPath));
        Assert.True(File.Exists(_f.Full("chars/Gaara/Gaara.def")));
        Assert.True(new DateAddedStore(Path.Combine(_f.AppDir, "date-added")).Read(_f.Root).Items
            .ContainsKey(ContentIdentity.ForCharacterFolder("Gaara")));
        Assert.Equal("Gaara.def", _primaryDefs.Get(_f.Root, "Gaara"));
        Assert.Equal(ContentStatus.Active, Index().Characters.Single(c => c.Id == "Gaara").Status);
    }

    [Fact]
    public void FailedSelectDefRestoreIsReportedWithTheBackupLocation()
    {
        Roster("Naruto", "Gaara");
        WriteSelect("[Characters]\nNaruto\nGaara\n");
        var faulty = new FaultyMutations(_f.Mutations) { FailDeleteDirectory = true, FailRollback = true };

        var result = Service(faulty).Delete(_f.Root, "Gaara");

        Assert.False(result.Success);
        Assert.False(result.SelectDefRestored);
        Assert.Contains("could not be restored automatically", result.Error);
        var backup = _f.Mutations.LoadManifest(result.SelectDefOperationId!)!.BackupPath!;
        Assert.Contains(backup, result.Error);
        Assert.Equal("[Characters]\nNaruto\nGaara\n", File.ReadAllText(backup));
        Assert.True(Directory.Exists(_f.Full("chars/Gaara")));
    }

    [Fact]
    public void SelectDefWriteFailureDeletesNothing()
    {
        Roster("Naruto", "Gaara");
        var bytes = WriteSelect("[Characters]\nNaruto\nGaara\n");
        var faulty = new FaultyMutations(_f.Mutations) { FailReplaceFile = true };

        var result = Service(faulty).Delete(_f.Root, "Gaara");

        Assert.False(result.Success);
        Assert.Contains("Could not update select.def", result.Error);
        Assert.Contains("Nothing was deleted", result.Error);
        Assert.Equal(bytes, File.ReadAllBytes(SelectPath));
        Assert.True(File.Exists(_f.Full("chars/Gaara/Gaara.def")));
        Assert.Equal(0, faulty.DeleteDirectoryCalls);
    }

    [Fact]
    public void FileOpenInsideTheFolderFailsCleanlyAndRestoresSelectDef()
    {
        // Windows refuses to rename a folder while a file inside is open without delete sharing;
        // other platforms allow it, so there is nothing to provoke there.
        if (!OperatingSystem.IsWindows()) return;

        Roster("Naruto", "Gaara", "Muzan");
        var bytes = WriteSelect("[Characters]\r\nNaruto\r\nGaara\r\nMuzan\r\n");
        var fingerprint = IkemenPathGuard.Sha256DirectoryFingerprint(_f.Full("chars/Gaara"));

        CharacterDeletionResult result;
        using (new FileStream(_f.Full("chars/Gaara/char.sff"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = Service().Delete(_f.Root, "Gaara");
        }

        Assert.False(result.Success);
        Assert.True(result.SelectDefRestored);
        Assert.Contains("in use", result.Error);
        Assert.Equal(bytes, File.ReadAllBytes(SelectPath));
        Assert.Equal(fingerprint, IkemenPathGuard.Sha256DirectoryFingerprint(_f.Full("chars/Gaara")));
        Assert.Empty(Leftovers("chars"));
    }

    [Fact]
    public void MissingFolderIsRefusedWithoutTouchingSelectDef()
    {
        Roster("Naruto");
        var bytes = WriteSelect("[Characters]\nNaruto\nGaara\n");

        var result = Service().Delete(_f.Root, "Gaara");

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Error);
        Assert.Equal(bytes, File.ReadAllBytes(SelectPath));
    }

    [Fact]
    public void LinkedCharacterFolderIsRefused()
    {
        var outside = Path.Combine(_f.AppDir, "outside-char");
        PrimaryDefResolverTests.WriteDef(outside, "Linked.def", "Linked");
        try
        {
            Directory.CreateSymbolicLink(_f.Full("chars/Linked"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // creating links needs privileges on some Windows setups
        }

        WriteSelect("[Characters]\nLinked\n");
        var result = Service().Delete(_f.Root, "Linked");

        Assert.False(result.Success);
        Assert.True(File.Exists(Path.Combine(outside, "Linked.def")));
        Assert.Equal("[Characters]\nLinked\n", ReadSelect());
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".ikemenlab-123.old")]
    public void InvalidCharacterIdsAreRefused(string id)
    {
        Roster("Naruto");
        WriteSelect("[Characters]\nNaruto\n");

        var plan = Service().Preview(_f.Root, id);

        Assert.False(plan.CanDelete);
        Assert.True(Directory.Exists(_f.Full("chars/Naruto")));
    }

    [Fact]
    public void PreviewReportsFolderRosterLinesAndChangesNothing()
    {
        Roster("Naruto", "Gaara");
        var bytes = WriteSelect("[Characters]\nNaruto\nGaara, order=2\n;Gaara/Gaara.def\n");

        var plan = Service().Preview(_f.Root, "Gaara", "Gaara of the Sand");

        Assert.True(plan.CanDelete, plan.Error);
        Assert.Equal("Gaara of the Sand", plan.DisplayName);
        Assert.Equal("chars/Gaara", plan.PackageFolder);
        Assert.Equal(Path.GetFullPath(_f.Full("chars/Gaara")), plan.PackageFullPath);
        Assert.Equal([3, 4], plan.RosterEntries.Select(r => r.LineNumber));
        Assert.True(plan.SizeBytes > 0);
        Assert.Equal(bytes, File.ReadAllBytes(SelectPath));
        Assert.True(Directory.Exists(_f.Full("chars/Gaara")));

        var message = plan.ConfirmationMessage();
        Assert.Contains("\"Gaara of the Sand\"", message);
        Assert.Contains("Folder: chars/Gaara", message);
        Assert.Contains(plan.PackageFullPath, message);
        Assert.Contains("line 3: Gaara, order=2", message);
        Assert.Contains("line 4: ;Gaara/Gaara.def   (disabled)", message);
        Assert.Contains("move up", message);
    }

    /// <summary>Delegates to the real service, injecting failures where a test asks for them.</summary>
    private sealed class FaultyMutations(ISafeMutationService inner) : ISafeMutationService
    {
        public const string InjectedError = "Injected folder failure.";

        public bool FailDeleteDirectory { get; init; }
        public bool FailReplaceFile { get; init; }
        public bool FailRollback { get; init; }
        public int DeleteDirectoryCalls { get; private set; }

        public OperationPlan PlanCreateFile(string r, string t, byte[] c) => inner.PlanCreateFile(r, t, c);
        public OperationPlan PlanReplaceFile(string r, string t, string s) => inner.PlanReplaceFile(r, t, s);
        public OperationPlan PlanCreateDirectory(string r, string t, string s) => inner.PlanCreateDirectory(r, t, s);
        public OperationPlan PlanReplaceDirectory(string r, string t, string s) => inner.PlanReplaceDirectory(r, t, s);
        public OperationPlan PlanDeleteDirectory(string r, string t) => inner.PlanDeleteDirectory(r, t);
        public MutationResult CreateFile(string r, string t, byte[] c, bool d = false) => inner.CreateFile(r, t, c, d);

        public MutationResult ReplaceFile(string r, string t, string s, bool d = false, string? expectedCurrentHash = null)
            => FailReplaceFile ? Failure(MutationKind.ReplaceFile, r, t, "Injected select.def failure.")
                : inner.ReplaceFile(r, t, s, d, expectedCurrentHash);

        public MutationResult CreateDirectory(string r, string t, string s, bool d = false) => inner.CreateDirectory(r, t, s, d);
        public MutationResult ReplaceDirectory(string r, string t, string s, bool d = false) => inner.ReplaceDirectory(r, t, s, d);

        public MutationResult DeleteDirectory(string r, string t, bool d = false)
        {
            DeleteDirectoryCalls++;
            return FailDeleteDirectory ? Failure(MutationKind.DeleteDirectory, r, t, InjectedError) : inner.DeleteDirectory(r, t, d);
        }

        public MutationResult Execute(OperationPlan p, byte[]? c = null, bool d = false) => inner.Execute(p, c, d);

        public MutationResult Rollback(string id)
            => FailRollback ? Failure(MutationKind.ReplaceFile, "", "", "Injected rollback failure.") : inner.Rollback(id);

        public MutationManifest? LoadManifest(string id) => inner.LoadManifest(id);

        private static MutationResult Failure(MutationKind kind, string root, string target, string error) => new()
        {
            Success = false,
            Manifest = new MutationManifest
            {
                OperationId = "injected-" + Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                Kind = kind,
                IkemenRoot = root,
                TargetPath = target,
                Status = MutationStatus.Failed,
                Error = error
            }
        };
    }
}
