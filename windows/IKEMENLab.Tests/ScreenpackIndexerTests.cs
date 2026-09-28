using IKEMENLab.Core.Screenpacks;
using Xunit;

namespace IKEMENLab.Tests;

public class ScreenpackIndexerTests
{
    [Fact]
    public void FindsMotifsDetectsComponentsAndActiveOne()
    {
        using var root = new TestRoot();
        root.Write("data/system.def", "[Info]\nname = Default\nlocalcoord = 320,240\n[Files]\nspr = system.sff\n[Select Info]\nrows = 2\ncolumns = 5\n");
        root.Write("data/system.sff", "x");
        root.Write("data/hd/system.def",
            "[Info]\nname = HD Pack\nauthor = Someone\nlocalcoord = 1280,720\n[Files]\nspr = hd.sff\nfight = fight.def\n" +
            "[Title Info]\n[Select Info]\nrows = 10\ncolumns = 10\n[VS Screen]\n[Option Info]\n");
        root.Write("data/hd/fight.def", "[Files]\n");
        root.Write("data/lifebars/system.def", "[Info]\nname = Bars Only\n[Files]\nfight = fight.def\n");
        root.Write("data/lifebars/fight.def", "[Files]\n");
        root.Write("data/notapack/readme.txt", "x");

        var packs = ScreenpackIndexer.Index(root.Path, "data/hd/system.def");

        Assert.Equal(3, packs.Count);
        var hd = packs[0];
        Assert.True(hd.IsActive);
        Assert.Equal("HD Pack", hd.Name);
        Assert.Equal("1280x720", hd.ResolutionText);
        Assert.Equal("100 slots (10×10)", hd.SlotsText);
        Assert.Equal(["Title", "Select", "VS", "Lifebars", "Options"], hd.ComponentNames());
        Assert.Null(hd.SpriteFile); // hd.sff referenced but missing

        var def = packs.Single(p => p.Id == "data");
        Assert.False(def.IsActive);
        Assert.EndsWith("system.sff", def.SpriteFile);

        Assert.Equal("Lifebar", packs.Single(p => p.Name == "Bars Only").PrimaryType);
    }

    [Fact]
    public void DefaultMotifIsActiveWhenConfigHasNone()
    {
        using var root = new TestRoot();
        root.Write("data/system.def", "[Info]\nname = Default\n");
        var packs = ScreenpackIndexer.Index(root.Path, null);
        Assert.True(Assert.Single(packs).IsActive);
    }
}
