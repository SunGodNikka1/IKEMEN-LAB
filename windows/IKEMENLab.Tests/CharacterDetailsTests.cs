using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Services;
using Xunit;
using static IKEMENLab.Tests.SffTestBuilder;

namespace IKEMENLab.Tests;

public class CharacterDetailsTests
{
    private const string Cmd = """
        ; comment
        [Command]
        name = "TripleKFPalm"
        command = ~D, DF, F, x+y
        time = 15

        [Command]
        name = "TripleKFPalm"
        command = ~D, F, x+y

        [Command]
        name = "QCF_x"
        command = ~D, DF, F, x
        command.time = 20

        [Command]
        name = "AI1"
        command = ~D, DF, F, U, U, U, x

        [Command]
        name = "FF"
        command = F, F

        [Command]
        name = "a"
        command = a

        [Statedef -1]
        """;

    [Fact]
    public void MoveListKeepsSpecialsAndSkipsAiAndBasics()
    {
        var moves = CmdMoveReader.Parse(Cmd);
        Assert.Equal(["QCF x", "Triple KFPalm"], moves.Select(m => m.DisplayName).OrderBy(x => x));
        var palm = moves.Single(m => m.Name == "TripleKFPalm");
        Assert.Equal("~D, DF, F, x+y", palm.Command);   // first definition wins
        Assert.Equal("↓↘→ + LP+MP", palm.Notation);
    }

    [Fact]
    public void NotationDoesNotConfuseBackWithMkButton()
    {
        // The Mac formatter turned the "b" button into a back arrow.
        Assert.Equal("↓↙← + MK", CmdMoveReader.Notation(["~D", "DB", "B", "b"]));
    }

    [Fact]
    public void HypersSortFirst()
    {
        var moves = CmdMoveReader.Parse("[Command]\nname = \"Fireball\"\ncommand = ~D, DF, F, x\n[Command]\nname = \"SuperFire\"\ncommand = ~D, DF, F, D, DF, F, x\n");
        Assert.True(moves[0].IsHyper);
    }

    [Theory]
    [InlineData("04,25,2009", "04/25/2009")]
    [InlineData("25.04.2009", "04/25/2009")]
    [InlineData("2009-04-25", "04/25/2009")]
    [InlineData("4/5/2010", "05/04/2010")] // ambiguous: day-first like the Mac order
    [InlineData("  ", "")]
    [InlineData("sometime in 2003", "sometime in 2003")]
    public void VersionDatesNormaliseLikeMac(string input, string expected)
    {
        Assert.Equal(expected, VersionDateFormatter.Format(input));
    }

    [Fact]
    public void StatsComeFromDataSectionWithDefaultsFlagged()
    {
        using var root = new TestRoot();
        var cns = root.Write("chars/k/k.cns", "[Data]\nlife = 1100\nattack = 95 ; weak\n[Size]\nxscale = 1\n[Statedef 0]\nlife = 5\n");
        var stats = CharacterDetailsReader.ReadStats(cns)!;
        Assert.Equal(new CnsValue(1100, false), stats.Life);
        Assert.Equal(new CnsValue(95, false), stats.Attack);
        Assert.Equal(new CnsValue(100, true), stats.Defence);
        Assert.Equal(new CnsValue(3000, true), stats.Power);
    }

    [Fact]
    public void MissingCnsGivesNoStatsInsteadOfInventedNumbers()
    {
        using var root = new TestRoot();
        root.Write("chars/k/k.def", "[Info]\nname = K\n[Files]\ncmd = k.cmd\ncns = k.cns\n");
        root.Write("chars/k/k.cmd", "");
        var character = new LibraryIndexService().Index(root.Path).Characters.Single();
        var details = CharacterDetailsReader.Read(root.Path, character);
        Assert.Null(details.Stats);
        Assert.Empty(details.Palettes);
        Assert.Equal("WinMUGEN", details.EngineLabel);
    }

    [Fact]
    public void PalettesCountRealActFilesWithSwatches()
    {
        using var root = new TestRoot();
        var pal = Palette((0, 0, 0), (200, 30, 30));
        root.Write("chars/k/k.def",
            "[Info]\nname = K\nmugenversion = 1.0\n[Files]\ncmd = k.cmd\ncns = k.cns\nsprite = k.sff\npal1 = p1.act\npal2 = missing.act\npal3 = p3.act\n");
        root.Write("chars/k/k.cmd", "");
        root.Write("chars/k/k.cns", "[Data]\nlife=1000\n");
        root.WriteBytes("chars/k/k.sff", BuildV1([new V1Sprite(0, 0, 2, 1, [1, 1], pal, false)]));
        var act = new byte[768];
        act[254 * 3 + 2] = 220; // index 1 = blue
        root.WriteBytes("chars/k/p1.act", act);
        root.WriteBytes("chars/k/p3.act", act);

        var character = new LibraryIndexService().Index(root.Path).Characters.Single();
        var details = CharacterDetailsReader.Read(root.Path, character);
        Assert.Equal([1, 3], details.Palettes.Select(p => p.Number));
        Assert.Equal(0xFF0000DCu, details.Palettes[0].SwatchArgb);
        Assert.Equal("MUGEN 1.0", details.EngineLabel);
    }

    [Fact]
    public void SffV2EmbeddedPalettesAreSelectable()
    {
        using var root = new TestRoot();
        root.Write("chars/k/k.def", "[Info]\nname = K\n[Files]\ncmd = k.cmd\ncns = k.cns\nsprite = k.sff\n");
        root.Write("chars/k/k.cmd", "");
        root.Write("chars/k/k.cns", "[Data]\n");
        var p1 = RgbaPalette((0, 0, 0, 0), (250, 10, 10, 0));
        var p2 = RgbaPalette((0, 0, 0, 0), (10, 250, 10, 0));
        root.WriteBytes("chars/k/k.sff", BuildV2([new V2Sprite(0, 0, 2, 1, 2, Rle8WithPrefix([1, 1]))], [p1, p2]));

        var character = new LibraryIndexService().Index(root.Path).Characters.Single();
        var details = CharacterDetailsReader.Read(root.Path, character);
        Assert.Equal([1, 2], details.Palettes.Select(p => p.Number));
        Assert.Equal(0xFF0AFA0Au, details.Palettes[1].SwatchArgb);
    }

    [Fact]
    public void FeatureBadgesComeFromRealFiles()
    {
        using var root = new TestRoot();
        root.Write("chars/k/k.def",
            "[Info]\nname = K\n[Files]\ncmd = k.cmd\ncns = k.cns\nsound = k.snd\n[Arcade]\nintro.storyboard = intro.def\n");
        root.Write("chars/k/k.cmd", "[Statedef -1]\n[State -1, x]\ntype = ChangeState\ntrigger1 = AILevel > 0\n");
        root.Write("chars/k/k.cns", "[Data]\n");
        root.Write("chars/k/k.snd", "x");
        root.Write("chars/k/intro.def", "[SceneDef]\n");

        root.Write("chars/plain/plain.def", "[Info]\nname = Plain\n[Files]\ncmd = p.cmd\ncns = p.cns\n");
        root.Write("chars/plain/p.cmd", "[Statedef -1]\n[State -1, walk]\ntype = ChangeState\n");
        root.Write("chars/plain/p.cns", "[Data]\n");

        var snapshot = new LibraryIndexService().Index(root.Path);
        var k = CharacterFeatureScanner.Scan(root.Path, snapshot.Characters.Single(c => c.Id == "k"));
        var plain = CharacterFeatureScanner.Scan(root.Path, snapshot.Characters.Single(c => c.Id == "plain"));
        Assert.Equal(["INTRO", "SFX", "AI"], k.Labels());
        Assert.Empty(plain.Labels());
    }
}
