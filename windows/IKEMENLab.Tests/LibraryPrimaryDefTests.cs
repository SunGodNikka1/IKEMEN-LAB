using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// The library and roster treat a character folder as one package whose primary DEF is decided by the
/// shared resolver, and they know which of its DEFs select.def actually uses.
/// </summary>
public sealed class LibraryPrimaryDefTests : IDisposable
{
    private readonly MutationFixture _f = MutationFixture.SameVolume();

    public void Dispose() => _f.Dispose();

    private string CharFolder(string name) => _f.Full("chars/" + name);

    private void Select(string characters) => _f.Write("data/select.def", "[Characters]\n" + characters + "\n\n[ExtraStages]\n");

    private LibrarySnapshot Index(PrimaryDefStore? store = null) => new LibraryIndexService(primaryDefs: store).Index(_f.Root);

    private CharacterEntry Character(LibrarySnapshot snapshot, string id)
        => snapshot.Characters.Single(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private void SeedMuzan()
    {
        PrimaryDefResolverTests.WriteDef(CharFolder("Muzan"), "Muzan.def", "Muzan");
        PrimaryDefResolverTests.WriteDef(CharFolder("Muzan"), "Muzan_AI.def", "Muzan");
        PrimaryDefResolverTests.WriteDef(CharFolder("Muzan"), "Muzan_noAI.def", "Muzan");
        PrimaryDefResolverTests.WriteKfmPlaceholder(CharFolder("Muzan"));
    }

    private void SeedTwins()
    {
        PrimaryDefResolverTests.WriteDef(CharFolder("Twins"), "Red.def", "Red", sprite: "red.sff");
        PrimaryDefResolverTests.WriteDef(CharFolder("Twins"), "Blue.def", "Blue", sprite: "blue.sff");
    }

    [Fact]
    public void AlternateDefOnTheRosterMakesTheCharacterActiveViaThatDef()
    {
        SeedMuzan();
        Select("Muzan/Muzan_AI.def");

        var muzan = Character(Index(), "Muzan");

        Assert.Equal(ContentStatus.Active, muzan.Status);
        Assert.Equal("chars/Muzan/Muzan_AI.def", muzan.DefPath);
        Assert.Equal(PrimaryDefRule.Roster, muzan.PrimaryRule);
        Assert.Equal(["chars/Muzan/Muzan_AI.def"], muzan.ActiveDefPaths);
        Assert.Equal(4, muzan.DefCandidates.Count);
    }

    [Fact]
    public void UnlistedPackageUsesTheResolverAndIgnoresThePlaceholder()
    {
        SeedMuzan();
        Select("kfm");

        var muzan = Character(Index(), "Muzan");

        Assert.Equal(ContentStatus.Unregistered, muzan.Status);
        Assert.Equal("chars/Muzan/Muzan.def", muzan.DefPath);
        Assert.False(muzan.NeedsDefChoice);
    }

    [Fact]
    public void AmbiguousPackageNeedsAChoiceUntilOneIsSaved()
    {
        SeedTwins();
        Select("kfm");
        var store = new PrimaryDefStore(Path.Combine(_f.AppDir, "primary-def"));

        var before = Character(Index(store), "Twins");
        Assert.True(before.NeedsDefChoice);
        Assert.Equal(PrimaryDefRule.Ambiguous, before.PrimaryRule);

        store.Set(_f.Root, "Twins", "Red.def");
        var after = Character(Index(store), "Twins");
        Assert.False(after.NeedsDefChoice);
        Assert.Equal("chars/Twins/Red.def", after.DefPath);
        Assert.Equal(PrimaryDefRule.SavedChoice, after.PrimaryRule);
        Assert.False(Directory.EnumerateFiles(_f.Root, "*.json", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void DisablingTheActiveAlternateCommentsThatLineAndKeepsItPreferred()
    {
        SeedMuzan();
        Select("kfm\nMuzan/Muzan_AI.def");
        var roster = new RosterActivationService(_f.Mutations, _f.StagingDir);

        var muzan = Character(Index(), "Muzan");
        Assert.True(roster.SetCharacterEnabled(_f.Root, muzan.ActiveDefPaths.Single(), false).Success);

        var disabled = Character(Index(), "Muzan");
        Assert.Equal(ContentStatus.Disabled, disabled.Status);
        Assert.Equal("chars/Muzan/Muzan_AI.def", disabled.DefPath);
        Assert.Contains(";Muzan/Muzan_AI.def", File.ReadAllText(_f.Full("data/select.def")));

        // Re-enabling the preferred DEF uncomments the same line rather than adding "Muzan".
        Assert.True(roster.SetCharacterEnabled(_f.Root, disabled.DefPath, true).Success);
        var text = File.ReadAllText(_f.Full("data/select.def"));
        Assert.Contains("\nMuzan/Muzan_AI.def", text);
        Assert.DoesNotContain("\nMuzan\n", text);
    }

    [Fact]
    public void NestedOnlyLayoutKeepsItsId()
    {
        PrimaryDefResolverTests.WriteDef(_f.Full("chars/Muzan/Muzan"), "Muzan.def", "Muzan");
        Select("Muzan/Muzan/Muzan.def");

        var muzan = Index().Characters.Single();

        Assert.Equal("Muzan/Muzan", muzan.Id);
        Assert.True(muzan.Nested);
        Assert.Equal(ContentStatus.Active, muzan.Status);
    }

    [Fact]
    public void CollectionActivationKeepsTheActiveAlternateAndDisablesNonMembersFully()
    {
        SeedMuzan();
        PrimaryDefResolverTests.WriteDef(CharFolder("Akaza"), "Akaza.def", "Akaza");
        PrimaryDefResolverTests.WriteDef(CharFolder("Akaza"), "Akaza_AI.def", "Akaza");
        Select("Muzan/Muzan_AI.def\nAkaza\nAkaza/Akaza_AI.def");

        var collections = new CollectionStore(Path.Combine(_f.AppDir, "collections.json"));
        var activation = new CollectionActivationService(_f.Mutations,
            new ActiveCollectionStore(Path.Combine(_f.AppDir, "active.json")), _f.StagingDir);
        var index = Index().Characters;
        var only = collections.Create(_f.Root, "Only Muzan");
        only = collections.Add(_f.Root, only.Id, [index.Single(c => c.Id == "Muzan")]);

        var preview = activation.Preview(_f.Root, only, index);
        Assert.True(preview.CanActivate, preview.Error);
        Assert.Equal(["chars/Muzan/Muzan_AI.def"], preview.AlreadyActive);
        Assert.Empty(preview.WillEnable);
        Assert.Equal(2, preview.WillDisable.Count);

        Assert.True(activation.Activate(_f.Root, only, index).Success);
        var after = Index();
        Assert.Equal(ContentStatus.Active, Character(after, "Muzan").Status);
        Assert.Equal(ContentStatus.Disabled, Character(after, "Akaza").Status);
        Assert.Empty(Character(after, "Akaza").ActiveDefPaths);
    }

    [Fact]
    public void CollectionActivationRefusesToGuessAnAmbiguousMembersDef()
    {
        SeedTwins();
        Select("kfm");
        var collections = new CollectionStore(Path.Combine(_f.AppDir, "collections.json"));
        var activation = new CollectionActivationService(_f.Mutations,
            new ActiveCollectionStore(Path.Combine(_f.AppDir, "active.json")), _f.StagingDir);
        var index = Index().Characters;
        var twins = collections.Create(_f.Root, "Twins only");
        twins = collections.Add(_f.Root, twins.Id, [index.Single(c => c.Id == "Twins")]);
        var before = File.ReadAllText(_f.Full("data/select.def"));

        var preview = activation.Preview(_f.Root, twins, index);

        Assert.False(preview.CanActivate);
        Assert.Contains("Choose which DEF", preview.Error);
        Assert.Equal(["chars/Twins/Blue.def"], preview.NeedsDefChoice);
        Assert.False(activation.Activate(_f.Root, twins, index).Success);
        Assert.Equal(before, File.ReadAllText(_f.Full("data/select.def")));
    }
}
