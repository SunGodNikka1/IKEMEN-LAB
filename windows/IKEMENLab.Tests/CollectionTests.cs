using System.Security.Cryptography;
using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public sealed class CollectionTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ikemenlab-collections-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_temp, "engine");
    private CollectionStore Store => new(Path.Combine(_temp, "appdata", "collections.json"));
    private static CharacterEntry Character(string id = "Ryu", string author = "Capcom", ContentStatus status = ContentStatus.Active)
        => new() { Id = id, Name = id, DisplayName = id, Author = author, VersionDate = "", DefPath = $"chars/{id}/{id}.def", FolderPath = $"chars/{id}", Status = status };

    [Fact]
    public void CreateReloadRenameDeletePreservesIdentityAndDates()
    {
        Assert.Empty(Store.Load(Root));
        Assert.False(Directory.Exists(_temp));
        var c = Store.Create(Root, "  Favorites  ");
        Assert.Equal("Favorites", c.Name);
        Assert.Equal(c.Id, Assert.Single(Store.Load(Root)).Id);
        var renamed = Store.Rename(Root, c.Id, "Main roster");
        Assert.Equal(c.CreatedAtUtc, renamed.CreatedAtUtc);
        Assert.True(renamed.ModifiedAtUtc >= c.ModifiedAtUtc);
        Assert.Equal("Main roster", Assert.Single(Store.Load(Root)).Name);
        Store.Delete(Root, c.Id);
        Assert.Empty(Store.Load(Root));
    }

    [Fact]
    public void DuplicateNamesAndEmptyNamesAreRejectedWithoutDataLoss()
    {
        var c = Store.Create(Root, "Favorites");
        var bytes = File.ReadAllBytes(Store.FilePath);
        Assert.Throws<InvalidOperationException>(() => Store.Create(Root, " favorites "));
        Assert.Throws<InvalidOperationException>(() => Store.Create(Root, " "));
        Assert.Throws<InvalidOperationException>(() => Store.Rename(Root, c.Id, "All Characters"));
        Assert.Equal(bytes, File.ReadAllBytes(Store.FilePath));
    }

    [Fact]
    public void MembersDeduplicateByFolderNotDisplayNameAndMissingMembersSurvive()
    {
        var c = Store.Create(Root, "Favorites");
        c = Store.Add(Root, c.Id, [Character("Pack/Ryu"), Character("pack\\ryu"), Character("Other/Ryu") with { DisplayName = "Ryu" }]);
        Assert.Equal(2, c.Members.Count);
        var resolved = CollectionMembership.Resolve(c, [Character("PACK/RYU")]);
        Assert.False(resolved[0].IsMissing);
        Assert.True(resolved[1].IsMissing);
        Assert.Equal(2, Assert.Single(Store.Load(Root)).Members.Count);
        c = Store.Remove(Root, c.Id, ["other\\ryu"]);
        Assert.Single(c.Members);
        Assert.False(Assert.Single(CollectionMembership.Resolve(c, [Character("Pack/Ryu")])).IsMissing);
    }

    [Fact]
    public void SameCharacterIdsFromAnotherInstallationDoNotLeak()
    {
        var first = Store.Create(Root, "Favorites");
        var other = Path.Combine(_temp, "other-engine");
        var second = Store.Create(other, "Favorites");
        Store.Add(Root, first.Id, [Character()]);
        Assert.Empty(Assert.Single(Store.Load(other)).Members);
        Assert.Throws<InvalidOperationException>(() => Store.Add(other, first.Id, [Character()]));
        Store.Delete(Root, first.Id);
        Assert.Equal(second.Id, Assert.Single(Store.Load(other)).Id);
    }

    [Fact]
    public void SmartRulesPersistAndReevaluateCurrentIndexWithoutWriting()
    {
        var c = Store.Create(Root, "Capcom", CollectionKind.Smart, [new(RuleField.Author, RuleComparison.Equals, "capcom")]);
        c = Assert.Single(Store.Load(Root));
        var bytes = File.ReadAllBytes(Store.FilePath);
        Assert.Single(CollectionMembership.Resolve(c, [Character(), Character("Ken", "Other")]));
        Assert.Equal(2, CollectionMembership.Resolve(c, [Character(), Character("Ken")]).Count);
        Assert.Equal(bytes, File.ReadAllBytes(Store.FilePath));
        Assert.Throws<InvalidOperationException>(() => Store.Add(Root, c.Id, [Character()]));
        Assert.Throws<InvalidOperationException>(() => Store.Remove(Root, c.Id, ["Ryu"]));
        c = Store.Edit(Root, c.Id, "Ken", [new(RuleField.Name, RuleComparison.Equals, "Ken")], RuleMatch.Any);
        Assert.Equal("Ken", Assert.Single(CollectionMembership.Resolve(c, [Character(), Character("Ken")])).Character!.Name);
        Assert.Empty(c.Members);
    }

    [Theory]
    [InlineData(RuleComparison.Contains, "YU", true)]
    [InlineData(RuleComparison.DoesNotContain, "Ken", true)]
    [InlineData(RuleComparison.Equals, "ryu", true)]
    [InlineData(RuleComparison.NotEquals, "Ryu", false)]
    [InlineData(RuleComparison.IsEmpty, "", false)]
    [InlineData(RuleComparison.IsNotEmpty, "", true)]
    public void TextRuleSemantics(RuleComparison comparison, string value, bool expected)
        => Assert.Equal(expected, SmartCollectionEvaluator.Matches(new(RuleField.Name, comparison, value), Character()));

    [Fact]
    public void AllAnyEmptyAndIndexedFields()
    {
        CollectionRule[] rules = [new(RuleField.Name, RuleComparison.Equals, "Ryu"), new(RuleField.Author, RuleComparison.Equals, "Other")];
        Assert.False(SmartCollectionEvaluator.Matches(rules, RuleMatch.All, Character()));
        Assert.True(SmartCollectionEvaluator.Matches(rules, RuleMatch.Any, Character()));
        Assert.True(SmartCollectionEvaluator.Matches([], RuleMatch.Any, Character()));
        Assert.True(SmartCollectionEvaluator.Matches([], RuleMatch.All, Character()));
        Assert.True(SmartCollectionEvaluator.Matches(new(RuleField.Author, RuleComparison.IsEmpty, ""), Character(author: "Unknown")));
        Assert.True(SmartCollectionEvaluator.Matches(new(RuleField.Folder, RuleComparison.Contains, "pack/"), Character("Pack\\Ryu")));
        Assert.True(SmartCollectionEvaluator.Matches(new(RuleField.Registration, RuleComparison.Equals, "unregistered"), Character(status: ContentStatus.Unregistered)));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("{\"schemaVersion\":999,\"collections\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"collections\":[null]}")]
    public void CorruptOrFutureDocumentsAreNotOverwritten(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!);
        File.WriteAllText(Store.FilePath, json);
        Assert.Throws<InvalidDataException>(() => Store.Load(Root));
        Assert.Throws<InvalidDataException>(() => Store.Create(Root, "Favorites"));
        Assert.Equal(json, File.ReadAllText(Store.FilePath));
    }

    [Fact]
    public void FailedSaveLeavesPreviousCollectionFileIntact()
    {
        var c = Store.Create(Root, "Favorites");
        var before = File.ReadAllBytes(Store.FilePath);
        // Deterministic boundary rejection, not platform-dependent file permissions.
        Assert.Throws<InvalidOperationException>(() => Store.Create(Path.GetDirectoryName(Store.FilePath)!, "Forbidden"));
        Assert.Equal(before, File.ReadAllBytes(Store.FilePath));
        Assert.Equal(c.Id, Assert.Single(Store.Load(Root)).Id);
    }

    [Fact]
    public void CollectionOperationsCannotWriteIntoInstallation()
    {
        BuildEngine();
        var before = Fingerprint(Root);
        var index = new LibraryIndexService().Index(Root);
        Assert.Single(index.Characters);
        var c = Store.Create(Root, "Favorites");
        c = Store.Add(Root, c.Id, index.Characters);
        _ = CollectionMembership.Resolve(c, index.Characters);
        Store.Rename(Root, c.Id, "Renamed");
        Store.Remove(Root, c.Id, [index.Characters[0].Id]);
        Store.Delete(Root, c.Id);
        var bad = new CollectionStore(Path.Combine(Root, "save", "collections.json"));
        Assert.Throws<InvalidOperationException>(() => bad.Create(Root, "Forbidden"));
        Assert.Equal(before, Fingerprint(Root));
    }

    private void BuildEngine()
    {
        Directory.CreateDirectory(Path.Combine(Root, "chars", "Ryu"));
        Directory.CreateDirectory(Path.Combine(Root, "data"));
        Directory.CreateDirectory(Path.Combine(Root, "save"));
        Directory.CreateDirectory(Path.Combine(Root, "stages"));
        File.WriteAllBytes(Path.Combine(Root, "Ikemen_GO.exe"), [0x4d, 0x5a]);
        File.WriteAllText(Path.Combine(Root, "chars", "Ryu", "Ryu.def"), "[Info]\nname=Ryu\nauthor=Capcom\n[Files]\ncmd=r.cmd\ncns=r.cns\n");
        File.WriteAllText(Path.Combine(Root, "data", "select.def"), "[Characters]\nRyu\n");
        File.WriteAllText(Path.Combine(Root, "save", "config.ini"), "[Config]\n");
    }

    private static string[] Fingerprint(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(p => p).Select(p => $"{p}|{new FileInfo(p).LastWriteTimeUtc.Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}").ToArray();

    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
}
