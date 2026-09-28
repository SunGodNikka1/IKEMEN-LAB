using IKEMENLab.Core.Library;
using Xunit;

namespace IKEMENLab.Tests;

public sealed class PrimaryDefResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ikemenlab-primary-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Writes a character DEF and (unless <paramref name="withFiles"/> is false) the files it references.</summary>
    internal static void WriteDef(string folder, string file, string name, string sprite = "char.sff",
        string author = "Tester", bool withFiles = true)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, file),
            $"[Info]\nname = \"{name}\"\nauthor = \"{author}\"\n\n[Files]\ncmd = char.cmd\ncns = char.cns\nsprite = {sprite}\nanim = char.air\n");
        if (!withFiles) return;
        foreach (var f in new[] { "char.cmd", "char.cns", "char.air", sprite })
        {
            var path = Path.Combine(folder, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, "x");
        }
    }

    internal static void WriteKfmPlaceholder(string folder, bool withFiles = false)
        => WriteDef(folder, "KFM.def", "Kung Fu Man", sprite: "kfm.sff", author: "Elecbyte", withFiles: withFiles);

    private string Folder(string name) => Path.Combine(_dir, name);

    [Fact]
    public void SingleDefIsPrimary()
    {
        var f = Folder("Solo");
        WriteDef(f, "whatever.def", "Solo");
        var d = PrimaryDefResolver.Resolve(f, "Solo");
        Assert.Equal(PrimaryDefRule.OnlyCandidate, d.Rule);
        Assert.Equal("whatever.def", d.Primary!.RelativePath);
    }

    [Fact]
    public void KfmPlaceholderCannotStealPrimaryAndFolderNameWins()
    {
        var f = Folder("Muzan");
        WriteDef(f, "Muzan.def", "Demon King Muzan");
        WriteDef(f, "Muzan_AI.def", "Demon King Muzan");
        WriteDef(f, "Muzan_noAI.def", "Demon King Muzan");
        WriteKfmPlaceholder(f);

        var d = PrimaryDefResolver.Resolve(f, "Muzan");

        Assert.Equal("Muzan.def", d.Primary!.RelativePath);
        Assert.Equal(PrimaryDefRule.FolderName, d.Rule);
        Assert.Equal(4, d.Candidates.Count);
        var kfm = d.Candidates.Single(c => c.FileName == "KFM.def");
        Assert.True(kfm.IsPlaceholder);
        Assert.False(kfm.IsComplete);
        Assert.Equal("AI", d.Candidates.Single(c => c.FileName == "Muzan_AI.def").AiTag);
        Assert.Equal("No AI", d.Candidates.Single(c => c.FileName == "Muzan_noAI.def").AiTag);
    }

    [Fact]
    public void AiAlternativesResolveToTheirBaseWhenTheFolderNameDoesNotMatch()
    {
        var f = Folder("Muzan V3");
        WriteDef(f, "Muzan.def", "Muzan");
        WriteDef(f, "Muzan_AI.def", "Muzan");
        WriteDef(f, "Muzan (No AI).def", "Muzan");
        WriteKfmPlaceholder(f, withFiles: true);

        var d = PrimaryDefResolver.Resolve(f, "Muzan V3");

        Assert.Equal(PrimaryDefRule.AiVariantBase, d.Rule);
        Assert.Equal("Muzan.def", d.Primary!.RelativePath);
    }

    [Fact]
    public void AiAlternativesWithoutABaseAreAmbiguous()
    {
        var f = Folder("Akuma");
        WriteDef(f, "Akuma_AI.def", "Akuma");
        WriteDef(f, "Akuma_noAI.def", "Akuma");

        var d = PrimaryDefResolver.Resolve(f, "Akuma");

        Assert.True(d.IsAmbiguous);
        Assert.Null(d.Primary);
        Assert.NotNull(d.DisplayCandidate);
    }

    [Fact]
    public void AiLookalikesWithDifferentSpritesAreAmbiguous()
    {
        var f = Folder("Pair");
        WriteDef(f, "Ryu.def", "Ryu", sprite: "ryu.sff");
        WriteDef(f, "Ryu_AI.def", "Evil Ryu", sprite: "evilryu.sff");

        Assert.True(PrimaryDefResolver.Resolve(f, "Pair").IsAmbiguous);
    }

    [Fact]
    public void UnrelatedDefsAreAmbiguousWithADeterministicDisplayCandidate()
    {
        var f = Folder("Twins");
        WriteDef(f, "Red.def", "Red", sprite: "red.sff");
        WriteDef(f, "Blue.def", "Blue", sprite: "blue.sff");

        var first = PrimaryDefResolver.Resolve(f, "Twins");
        var second = PrimaryDefResolver.Resolve(f, "Twins");

        Assert.True(first.IsAmbiguous);
        Assert.Equal("Blue.def", first.DisplayCandidate!.RelativePath);
        Assert.Equal(first.DisplayCandidate!.RelativePath, second.DisplayCandidate!.RelativePath);
    }

    [Fact]
    public void CompleteKfmIsStillOnlyAPlaceholderNextToARealCharacter()
    {
        var f = Folder("Pack");
        WriteKfmPlaceholder(f, withFiles: true);
        WriteDef(f, "Hero.def", "Hero", sprite: "hero.sff");

        var d = PrimaryDefResolver.Resolve(f, "Pack");

        Assert.Equal(PrimaryDefRule.OnlyCandidate, d.Rule);
        Assert.Equal("Hero.def", d.Primary!.RelativePath);
    }

    [Fact]
    public void KfmAloneIsTheCharacter()
    {
        var f = Folder("kfm");
        WriteDef(f, "kfm.def", "Kung Fu Man", sprite: "kfm.sff", author: "Elecbyte");
        var d = PrimaryDefResolver.Resolve(f, "kfm");
        Assert.Equal("kfm.def", d.Primary!.RelativePath);
    }

    [Fact]
    public void IncompleteDefLosesToACompleteOne()
    {
        var f = Folder("Broken");
        WriteDef(f, "Old.def", "Old", sprite: "gone.sff", withFiles: false);
        WriteDef(f, "New.def", "New", sprite: "new.sff");

        var d = PrimaryDefResolver.Resolve(f, "Broken");

        Assert.Equal("New.def", d.Primary!.RelativePath);
        Assert.Contains("gone.sff", d.Candidates.Single(c => c.FileName == "Old.def").MissingFiles);
    }

    [Fact]
    public void SavedChoiceWinsAndAStaleOneIsIgnored()
    {
        var f = Folder("Muzan");
        WriteDef(f, "Muzan.def", "Muzan");
        WriteDef(f, "Muzan_AI.def", "Muzan");

        var saved = PrimaryDefResolver.Resolve(f, "Muzan", new PrimaryDefHints { SavedChoice = "Muzan_AI.def" });
        Assert.Equal(PrimaryDefRule.SavedChoice, saved.Rule);
        Assert.Equal("Muzan_AI.def", saved.Primary!.RelativePath);

        var stale = PrimaryDefResolver.Resolve(f, "Muzan", new PrimaryDefHints { SavedChoice = "Removed.def" });
        Assert.Equal("Muzan.def", stale.Primary!.RelativePath);
    }

    [Fact]
    public void RosterUseDecidesIncludingDeeperDefs()
    {
        var f = Folder("Zenitsu");
        WriteDef(f, "Zenitsu.def", "Zenitsu");
        WriteDef(Path.Combine(f, "AI patch"), "Zenitsu.def", "Zenitsu");

        var d = PrimaryDefResolver.Resolve(f, "Zenitsu", new PrimaryDefHints { ActiveRoster = ["AI patch/Zenitsu.def"] });

        Assert.Equal(PrimaryDefRule.Roster, d.Rule);
        Assert.Equal("AI patch/Zenitsu.def", d.Primary!.RelativePath);

        var disabledOnly = PrimaryDefResolver.Resolve(f, "Zenitsu", new PrimaryDefHints { DisabledRoster = ["AI patch/Zenitsu.def"] });
        Assert.Equal("AI patch/Zenitsu.def", disabledOnly.Primary!.RelativePath);

        var twoActive = PrimaryDefResolver.Resolve(f, "Zenitsu",
            new PrimaryDefHints { ActiveRoster = ["Zenitsu.def", "AI patch/Zenitsu.def"] });
        Assert.Equal(PrimaryDefRule.FolderName, twoActive.Rule);
    }

    [Fact]
    public void NestedLayoutUsesTheLevelBelow()
    {
        var f = Folder("Muzan");
        WriteDef(Path.Combine(f, "Muzan"), "Muzan.def", "Muzan");
        var d = PrimaryDefResolver.Resolve(f, "Muzan");
        Assert.Equal("Muzan/Muzan.def", d.Primary!.RelativePath);
    }

    [Theory]
    [InlineData("Muzan_AI", "AI", "Muzan")]
    [InlineData("Muzan_noAI", "No AI", "Muzan")]
    [InlineData("Muzan-No-AI", "No AI", "Muzan")]
    [InlineData("Muzan (AI)", "AI", "Muzan")]
    [InlineData("Muzan (No AI)", "No AI", "Muzan")]
    [InlineData("MuzanAI", "AI", "Muzan")]
    [InlineData("MuzanNoAI", "No AI", "Muzan")]
    [InlineData("Sai_AI", "AI", "Sai")]
    [InlineData("Kai", null, null)]
    [InlineData("Sai", null, null)]
    [InlineData("KAI", null, null)]
    [InlineData("Muzan_AI_v2", null, null)]
    public void AiTagParsing(string stem, string? tag, string? baseName)
    {
        var (t, b) = PrimaryDefResolver.ParseAiTag(stem);
        Assert.Equal(tag, t);
        Assert.Equal(baseName, b);
    }

    [Fact]
    public void StoreKeepsChoicesPerInstallationOutsideTheRoot()
    {
        var appDir = Path.Combine(_dir, "app");
        var store = new PrimaryDefStore(appDir);
        var rootA = Path.Combine(_dir, "ikemenA");
        var rootB = Path.Combine(_dir, "ikemenB");

        store.Set(rootA, "Muzan", "Muzan_AI.def");
        store.Set(rootB, "Muzan", "Muzan.def");

        Assert.Equal("Muzan_AI.def", new PrimaryDefStore(appDir).Get(rootA, "muzan"));
        Assert.Equal("Muzan.def", store.Get(rootB, "MUZAN"));
        Assert.StartsWith(appDir, store.PathFor(rootA), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(rootA));

        store.Remove(rootA, "Muzan");
        Assert.Null(store.Get(rootA, "Muzan"));
    }
}
