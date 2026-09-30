using IKEMENLab.Core.Sprites;
using Xunit;
using static IKEMENLab.Tests.SffTestBuilder;

namespace IKEMENLab.Tests;

public class SpriteInspectorTests
{
    private static SffFile Open(byte[] bytes) => SffFile.Open(new MemoryStream(bytes))!;

    private static readonly byte[] Pal = Palette((0, 0, 0), (255, 0, 0), (0, 255, 0));

    [Fact]
    public void V1TableReportsSizeAxisAndEncoding()
    {
        var sff = Open(BuildV1([
            new V1Sprite(0, 0, 2, 2, [1, 2, 1, 2], Pal, false, AxisX: 20, AxisY: 40),
            new V1Sprite(9000, 0, 1, 1, [1], null, true)]));
        var report = SpriteInspector.Inspect(sff, "x.sff", isCharacter: true)!;
        Assert.Equal(1, report.Version);
        var first = report.Sprites[0];
        Assert.Equal(("0,0", 2, 2, 20, 40, "PCX"), (first.Id, first.Width, first.Height, first.AxisX, first.AxisY, first.Encoding));
        Assert.All(report.Sprites, s => Assert.True(s.Decodes));
        Assert.Equal([0, 9000], report.Groups.Select(g => g.Group));
        Assert.Contains(report.Findings, f => f.Message == "No problems found.");
    }

    [Fact]
    public void CharacterChecksFlagMissingStandingAndPortrait()
    {
        var sff = Open(BuildV1([new V1Sprite(5, 0, 1, 1, [1], Pal, false)]));
        var messages = SpriteInspector.Inspect(sff, "x.sff", isCharacter: true)!.Findings.Select(f => f.Message).ToList();
        Assert.Contains(messages, m => m.Contains("0,0"));
        Assert.Contains(messages, m => m.Contains("9000,0"));
        // Not a character SFF (stage): no character-only advice.
        Assert.DoesNotContain(SpriteInspector.Inspect(sff, "x.sff")!.Findings, f => f.Message.Contains("9000,0"));
    }

    [Fact]
    public void DuplicatesAreReported()
    {
        var sff = Open(BuildV1([
            new V1Sprite(0, 0, 1, 1, [1], Pal, false),
            new V1Sprite(0, 0, 1, 1, [2], null, true)]));
        var report = SpriteInspector.Inspect(sff, "x.sff")!;
        Assert.Contains(report.Findings, f => f.Severity == FindingSeverity.Warning && f.Message.Contains("more than once"));
    }

    [Fact]
    public void V2ReportsEncodingsLinksAndPalettes()
    {
        var pal = RgbaPalette((0, 0, 0, 0), (255, 0, 0, 255), (0, 0, 255, 255));
        var sff = Open(BuildV2([
            new V2Sprite(0, 0, 2, 1, 2, Rle8WithPrefix([1, 2]), AxisX: 5, AxisY: 9),
            new V2Sprite(0, 1, 2, 1, 0, [1, 2]),
            new V2Sprite(0, 2, 2, 1, 2, [], Link: 0)], [pal]));
        var report = SpriteInspector.Inspect(sff, "x.sff")!;
        Assert.Equal(2, report.Version);
        Assert.Equal(1, report.PaletteCount);
        Assert.Equal(["RLE8", "Raw 8-bit", "RLE8"], report.Sprites.Select(s => s.Encoding));
        Assert.Equal((5, 9), (report.Sprites[0].AxisX, report.Sprites[0].AxisY));
        Assert.Equal(0, report.Sprites[2].LinkedTo);
        Assert.All(report.Sprites, s => Assert.True(s.Decodes));
    }

    [Fact]
    public void UndecodableSpriteIsAnError_AndSkippingVerificationLeavesItUnknown()
    {
        var pal = RgbaPalette((0, 0, 0, 0), (255, 0, 0, 255));
        var bytes = BuildV2([new V2Sprite(0, 0, 1, 1, 99, [1, 2, 3, 4])], [pal]);
        var verified = SpriteInspector.Inspect(Open(bytes), "x.sff")!;
        Assert.False(verified.Sprites[0].Decodes);
        Assert.Equal("Unknown (99)", verified.Sprites[0].Encoding);
        Assert.Contains(verified.Findings, f => f.Severity == FindingSeverity.Error && f.Message.Contains("0,0"));
        Assert.Null(SpriteInspector.Inspect(Open(bytes), "x.sff", verifyDecode: false)!.Sprites[0].Decodes);
    }

    [Fact]
    public void RenderPngRoundTripsAndPaletteOverrideRecolors()
    {
        var pal = RgbaPalette((0, 0, 0, 0), (255, 0, 0, 255));
        var sff = Open(BuildV2([new V2Sprite(0, 0, 1, 1, 2, Rle8WithPrefix([1]))], [pal]));
        var sprite = sff.Sprites[0];

        var png = SpriteInspector.RenderPng(sff, sprite)!;
        var decoded = Png.Decode(png)!;
        Assert.Equal((0, 0, 255, 255), (decoded.Bgra[0], decoded.Bgra[1], decoded.Bgra[2], decoded.Bgra[3]));

        var green = new uint[256];
        green[1] = 0xFF00FF00;
        var recolored = Png.Decode(SpriteInspector.RenderPng(sff, sprite, green)!)!;
        Assert.Equal((0, 255, 0, 255), (recolored.Bgra[0], recolored.Bgra[1], recolored.Bgra[2], recolored.Bgra[3]));
    }

    [Fact]
    public void NotAnSffGivesNull()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not an sff");
            Assert.Null(SpriteInspector.Inspect(path));
        }
        finally { File.Delete(path); }
    }
}
