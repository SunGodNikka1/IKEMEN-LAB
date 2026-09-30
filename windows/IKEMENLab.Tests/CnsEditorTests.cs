using System.Text;
using IKEMENLab.Core.Characters;
using Xunit;

namespace IKEMENLab.Tests;

public class CnsEditorTests
{
    private const string Cns = "; Kung Fu Man\r\n[Data]\r\nlife = 1000   ; hit points\r\npower = 3000\r\nattack = 100\r\n\r\n[Size]\r\nxscale = 1\r\nyscale = 1\r\nground.back = 15\r\n\r\n[Statedef 0]\r\ntype = S\r\n";

    private static string Set(string text, params (string S, string K, string V)[] e)
    {
        var r = CnsEditor.SetValues(text, e.Select(x => new CnsEdit(x.S, x.K, x.V)).ToList());
        Assert.True(r.Success, r.Error);
        return r.Content!;
    }

    [Fact]
    public void EditingOneValueChangesOnlyThatLine()
    {
        var edited = Set(Cns, ("Data", "life", "1200"));
        Assert.Equal(Cns.Replace("life = 1000   ; hit points", "life = 1200   ; hit points"), edited);
    }

    [Fact]
    public void KeyMatchingIsCaseInsensitiveAndKeepsSpelling()
    {
        var edited = Set("[data]\nLife=900\n[Statedef 0]\n", ("Data", "life", "700"));
        Assert.Equal("[data]\nLife=700\n[Statedef 0]\n", edited);
    }

    [Fact]
    public void LastDuplicateIsTheOneThatCounts()
    {
        var text = "[Size]\nxscale = 1\nxscale = 0.8\n";
        Assert.Equal("[Size]\nxscale = 1\nxscale = 1.2\n", Set(text, ("Size", "xscale", "1.2")));
        Assert.Equal("0.8", CnsEditor.ReadValues(text)[("size", "xscale")]);
    }

    [Fact]
    public void MissingKeyIsAddedAtTheEndOfItsSectionBeforeBlankLines()
    {
        var edited = Set(Cns, ("Size", "height", "70"));
        Assert.Contains("ground.back = 15\r\nheight = 70\r\n\r\n[Statedef 0]", edited);
    }

    [Fact]
    public void MissingSectionGoesBeforeTheFirstStatedef()
    {
        var text = "[Data]\nlife = 1000\n\n[Statedef 0]\ntype = S\n";
        var edited = Set(text, ("Size", "xscale", "1.5"));
        Assert.Equal("[Data]\nlife = 1000\n\n[Size]\nxscale = 1.5\n\n[Statedef 0]\ntype = S\n", edited);
    }

    [Fact]
    public void MissingSectionWithoutStatedefIsAppended_AndNoFinalNewlineIsKept()
    {
        var edited = Set("[Data]\nlife = 1000", ("Size", "yscale", "2"));
        Assert.StartsWith("[Data]\nlife = 1000\n", edited);
        Assert.Contains("[Size]\nyscale = 2\n", edited);
    }

    [Fact]
    public void MixedLineEndingsSurviveOutsideTheEditedLine()
    {
        var text = "[Data]\r\nlife = 1000\nattack = 100\r\n";
        Assert.Equal("[Data]\r\nlife = 1000\nattack = 120\r\n", Set(text, ("Data", "attack", "120")));
    }

    [Fact]
    public void ValuesThatCouldBreakTheFileAreRejected()
    {
        var r = CnsEditor.SetValues(Cns, [new CnsEdit("Data", "life", "1;2")]);
        Assert.False(r.Success);
        Assert.False(CnsEditor.SetValues(Cns, [new CnsEdit("Data", "life", "1\n[Statedef 1]")]).Success);
    }

    [Fact]
    public void ReadValuesStripsCommentsAndIgnoresStates()
    {
        var v = CnsEditor.ReadValues(Cns);
        Assert.Equal("1000", v[("data", "life")]);
        Assert.Equal("S", v[("statedef 0", "type")]);
    }

    [Fact]
    public void NumbersFormatInvariantly()
    {
        Assert.Equal("1.25", CnsEditor.FormatNumber(1.25, false));
        Assert.Equal("2", CnsEditor.FormatNumber(2.0, false));
        Assert.Equal("1201", CnsEditor.FormatNumber(1200.6, true));
        Assert.DoesNotContain(",", CnsEditor.FormatNumber(0.5, false));
    }
}
