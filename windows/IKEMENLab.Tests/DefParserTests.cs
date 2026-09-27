using IKEMENLab.Core.Parsing;
using Xunit;

namespace IKEMENLab.Tests;

public class DefParserTests
{
    [Fact]
    public void ParseSimpleKeyValue()
    {
        var result = DefParser.Parse("name = Ryu\nauthor = Capcom\n");
        Assert.Equal("Ryu", result.Name);
        Assert.Equal("Capcom", result.Author);
    }

    [Fact]
    public void ParseWithWhitespace()
    {
        var result = DefParser.Parse("  name   =   Ryu with Spaces  \nauthor=NoSpaces\n");
        Assert.Equal("Ryu with Spaces", result.Name);
        Assert.Equal("NoSpaces", result.Author);
    }

    [Fact]
    public void ParseWithQuotedValues()
    {
        var result = DefParser.Parse("name = \"Street Fighter Ryu\"\ndisplayname = \"Ryu\"\n");
        Assert.Equal("Street Fighter Ryu", result.Name);
        Assert.Equal("Ryu", result.DisplayName);
    }

    [Fact]
    public void ParseIgnoresComments()
    {
        var result = DefParser.Parse("; comment\nname = Ryu\nauthor = Capcom ; inline\n");
        Assert.Equal("Ryu", result.Name);
        Assert.Equal("Capcom", result.Author);
    }

    [Fact]
    public void ParseIgnoresEmptyLines()
    {
        var result = DefParser.Parse("name = Ryu\n\n\nauthor = Capcom\n");
        Assert.Equal("Ryu", result.Name);
        Assert.Equal("Capcom", result.Author);
    }

    [Fact]
    public void ParseSections()
    {
        var result = DefParser.Parse("[Info]\nname = Ryu\nauthor = Capcom\n\n[Files]\nsprite = ryu.sff\ncmd = ryu.cmd\n");
        Assert.Equal("Ryu", result.Value("name", "info"));
        Assert.Equal("Capcom", result.Value("author", "info"));
        Assert.Equal("ryu.sff", result.Value("sprite", "files"));
        Assert.Equal("ryu.cmd", result.Value("cmd", "files"));
    }

    [Fact]
    public void ParseSectionsCaseInsensitive()
    {
        var result = DefParser.Parse("[INFO]\nName = Ryu\nAUTHOR = Capcom\n");
        Assert.Equal("Ryu", result.Value("name", "info"));
        Assert.Equal("Capcom", result.Value("author", "INFO"));
    }

    [Fact]
    public void ParseFlatValuesContainAllKeys()
    {
        var result = DefParser.Parse("[Info]\nname = Ryu\n\n[Files]\nsprite = ryu.sff\n");
        Assert.Equal("Ryu", result.Values["name"]);
        Assert.Equal("ryu.sff", result.Values["sprite"]);
    }

    [Fact]
    public void IntValue()
    {
        var result = DefParser.Parse("life = 1000\nattack = 100\ninvalid = abc\n");
        Assert.Equal(1000, result.IntValue("life"));
        Assert.Equal(100, result.IntValue("attack"));
        Assert.Equal(0, result.IntValue("invalid"));
        Assert.Equal(500, result.IntValue("missing", defaultValue: 500));
    }

    [Fact]
    public void SpriteFileAccessor()
    {
        Assert.Equal("ryu.sff", DefParser.Parse("[Files]\nsprite = ryu.sff\n").SpriteFile);
        Assert.Equal("stage.sff", DefParser.Parse("[BGdef]\nspr = stage.sff\n").SpriteFile);
    }

    [Fact]
    public void EffectiveName()
    {
        Assert.Equal("Internal Name", DefParser.Parse("name = Internal Name\ndisplayname = Display Name\n").EffectiveName);
        Assert.Equal("Only Display", DefParser.Parse("displayname = Only Display\n").EffectiveName);
    }

    [Fact]
    public void LastValueWinsDuplicates()
    {
        Assert.Equal("Second", DefParser.Parse("name = First\nname = Second\n").Name);
    }
}
