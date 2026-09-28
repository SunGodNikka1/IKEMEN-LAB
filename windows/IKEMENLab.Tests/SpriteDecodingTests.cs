using IKEMENLab.Core.Models;
using IKEMENLab.Core.Services;
using IKEMENLab.Core.Sprites;
using Xunit;
using static IKEMENLab.Tests.SffTestBuilder;

namespace IKEMENLab.Tests;

public class SpriteDecodingTests
{
    private static (byte B, byte G, byte R, byte A) Pixel(SpriteImage img, int x, int y)
    {
        var o = (y * img.Width + x) * 4;
        return (img.Bgra[o], img.Bgra[o + 1], img.Bgra[o + 2], img.Bgra[o + 3]);
    }

    [Fact]
    public void Rle8MatchesEngine()
    {
        // 0x43 = run of 3 of the next byte; plain bytes are literals.
        var p = SffCodecs.Rle8([0x43, 0x07, 0x01, 0x02], 5, 1);
        Assert.Equal([7, 7, 7, 1, 2], p);
    }

    [Fact]
    public void Rle5MatchesEngine()
    {
        var p = SffCodecs.Rle5([0x03, 0x80, 0x07], 4, 1);
        Assert.Equal([7, 7, 7, 7], p);
    }

    [Fact]
    public void Lz5LiteralsAndBackReference()
    {
        // ct=0b10: op0 literal (3 x colour 1), op1 short copy (3 bytes, distance 1).
        var p = SffCodecs.Lz5([0x02, 0x61, 0x02, 0x00], 6, 1);
        Assert.Equal([1, 1, 1, 1, 1, 1], p);
    }

    [Fact]
    public void PcxHonoursBytesPerLinePadding()
    {
        var p = SffCodecs.PcxRle([0xC3, 0x05, 0x09, 0x01, 0x02, 0x03, 0x00], 3, 2, bytesPerLine: 4);
        Assert.Equal([5, 5, 5, 1, 2, 3], p);
    }

    [Fact]
    public void TruncatedStreamsTerminate()
    {
        Assert.Equal(64, SffCodecs.Rle8([0x40], 8, 8).Length);
        Assert.Equal(64, SffCodecs.Lz5([0xFF, 0x00], 8, 8).Length);
        Assert.Equal(64, SffCodecs.Rle5([0x00], 8, 8).Length);
    }

    [Fact]
    public void PngRoundTrip()
    {
        var bgra = new byte[3 * 2 * 4];
        for (var i = 0; i < bgra.Length; i++) bgra[i] = (byte)(i * 11);
        var image = new SpriteImage(3, 2, bgra);
        var decoded = Png.Decode(Png.Encode(image));
        Assert.NotNull(decoded);
        Assert.Equal(bgra, decoded!.Bgra);
    }

    [Fact]
    public void V1UsesPreviousPaletteChainAndLinks()
    {
        var palA = Palette((0, 0, 0), (255, 0, 0));
        var palB = Palette((0, 0, 0), (0, 0, 255));
        var bytes = BuildV1(
        [
            new V1Sprite(0, 0, 2, 2, [1, 1, 1, 0], palA, SamePalette: false),
            new V1Sprite(9000, 0, 2, 1, [1, 1], null, SamePalette: true),
            new V1Sprite(9000, 1, 2, 1, [1, 0], palB, SamePalette: false),
            new V1Sprite(9000, 2, 0, 0, null, null, SamePalette: false, Link: 2)
        ]);

        using var sff = SffFile.Open(new MemoryStream(bytes))!;
        Assert.Equal(1, sff.Version);
        Assert.Equal(4, sff.Sprites.Count);

        var red = sff.Decode(sff.Find(9000, 0)!)!;
        Assert.Equal((0, 0, 255, 255), Pixel(red, 0, 0));      // palette A via "same palette"

        var blue = sff.Decode(sff.Find(9000, 1)!)!;
        Assert.Equal((255, 0, 0, 255), Pixel(blue, 0, 0));     // own palette B
        Assert.Equal(0, Pixel(blue, 1, 0).A);                  // index 0 transparent

        var linked = sff.Decode(sff.Find(9000, 2)!)!;
        Assert.Equal(blue.Bgra, linked.Bgra);
        Assert.Equal((2, 1), sff.Dimensions(sff.Find(9000, 1)!));
    }

    [Fact]
    public void V1Pal1ActReplacesPalette0Only()
    {
        var palA = Palette((0, 0, 0), (255, 0, 0));
        var bytes = BuildV1(
        [
            new V1Sprite(0, 0, 1, 1, [1], palA, SamePalette: false),
            new V1Sprite(9000, 1, 1, 1, [1], null, SamePalette: true)
        ]);
        // ACT is stored reversed: file entry 254 (last-but-one triple) becomes palette index 1.
        var act = new byte[768];
        act[254 * 3 + 1] = 200;
        var pal1 = ActPalette.Parse(act);
        Assert.Equal(0xFF00C800u, pal1[1]);

        using var sff = SffFile.Open(new MemoryStream(bytes))!;
        var img = sff.Decode(sff.Find(9000, 1)!, pal1)!;
        Assert.Equal((0, 200, 0, 255), Pixel(img, 0, 0));
    }

    [Fact]
    public void V2DecodesRle8PngAndForcedAlpha()
    {
        var pal = RgbaPalette((9, 9, 9, 0), (10, 20, 30, 0), (40, 50, 60, 0));
        var indexedPng = IndexedPng(2, 1, [2, 1], Palette((0, 0, 0), (1, 1, 1), (2, 2, 2)));
        var bytes = BuildV2(
        [
            new V2Sprite(0, 0, 3, 1, 2, Rle8WithPrefix([1, 1, 0])),
            new V2Sprite(9000, 1, 2, 1, 10, WithPrefix(indexedPng)),
            new V2Sprite(9000, 0, 3, 1, 0, [], Link: 0)
        ], [pal]);

        using var sff = SffFile.Open(new MemoryStream(bytes))!;
        Assert.Equal(2, sff.Version);

        var standing = sff.Decode(sff.Find(0, 0)!)!;
        Assert.Equal((30, 20, 10, 255), Pixel(standing, 0, 0)); // v2.00 forces opaque
        Assert.Equal(0, Pixel(standing, 2, 0).A);

        var png8 = sff.Decode(sff.Find(9000, 1)!)!;              // PNG indices + SFF palette
        Assert.Equal((60, 50, 40, 255), Pixel(png8, 0, 0));

        var linked = sff.Decode(sff.Find(9000, 0)!)!;
        Assert.Equal(standing.Bgra, linked.Bgra);
    }

    [Fact]
    public void V201UsesStoredPaletteAlpha()
    {
        var pal = RgbaPalette((0, 0, 0, 0), (10, 20, 30, 128));
        var bytes = BuildV2([new V2Sprite(0, 0, 1, 1, 2, Rle8WithPrefix([1]))], [pal], versionLo2: 1);
        using var sff = SffFile.Open(new MemoryStream(bytes))!;
        Assert.Equal(128, Pixel(sff.Decode(sff.Sprites[0])!, 0, 0).A);
    }

    [Fact]
    public void PortraitPrefersNormalSized9000_1()
    {
        var pal = Palette((0, 0, 0), (255, 255, 255));
        byte[] Fill(int n) => Enumerable.Repeat((byte)1, n).ToArray();
        var bytes = BuildV1(
        [
            new V1Sprite(0, 0, 4, 4, Fill(16), pal, false),
            new V1Sprite(9000, 0, 25, 25, Fill(625), pal, false),
            new V1Sprite(9000, 1, 120, 140, Fill(120 * 140), pal, false)
        ]);

        using var sff = SffFile.Open(new MemoryStream(bytes))!;
        var portrait = SpriteExtractor.ExtractPortrait(sff)!;
        Assert.Equal((9000, 1), (portrait.Group, portrait.Number));
        Assert.Equal(PortraitSource.Group9000, portrait.Source);
    }

    [Fact]
    public void PortraitFallsBackToTrimmedStandingSprite()
    {
        var pal = Palette((0, 0, 0), (255, 255, 255));
        var bytes = BuildV1([new V1Sprite(0, 0, 4, 3, [0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0], pal, false)]);
        using var sff = SffFile.Open(new MemoryStream(bytes))!;
        var portrait = SpriteExtractor.ExtractPortrait(sff)!;
        Assert.Equal(PortraitSource.StandingSprite, portrait.Source);
        Assert.Equal((2, 1), (portrait.Image.Width, portrait.Image.Height));
    }

    [Fact]
    public void StagePreviewPicksLargestOpaqueSprite()
    {
        var pal = Palette((0, 0, 0), (0, 128, 0));
        byte[] Fill(int n) => Enumerable.Repeat((byte)1, n).ToArray();
        var bytes = BuildV1(
        [
            new V1Sprite(0, 0, 2, 2, Fill(4), pal, false),
            new V1Sprite(1, 0, 16, 8, Fill(128), pal, false)
        ]);
        using var sff = SffFile.Open(new MemoryStream(bytes))!;
        var preview = SpriteExtractor.ExtractStagePreview(sff)!;
        Assert.Equal((16, 8), (preview.Image.Width, preview.Image.Height));
    }

    [Fact]
    public void StagePreviewRecombinesChannelSplitLayers()
    {
        // Three single-channel ramps (B, G, R) that the stage draws with trans=add.
        byte[] Ramp() => Enumerable.Range(0, 16 * 16).Select(i => (byte)(1 + i % 200)).ToArray();
        byte[] Pal(int channel)
        {
            var p = new byte[768];
            for (var i = 1; i < 256; i++) p[i * 3 + channel] = (byte)i;
            return p;
        }

        var bytes = BuildV1(
        [
            new V1Sprite(0, 0, 16, 16, Ramp(), Pal(2), false), // blue layer (RGB order: index 2 = B)
            new V1Sprite(0, 1, 16, 16, Ramp(), Pal(1), false), // green
            new V1Sprite(0, 2, 16, 16, Ramp(), Pal(0), false)  // red
        ]);
        using var sff = SffFile.Open(new MemoryStream(bytes))!;
        var preview = SpriteExtractor.ExtractStagePreview(sff)!;
        var (b, g, r, _) = Pixel(preview.Image, 5, 3);
        Assert.True(b > 0 && g > 0 && r > 0, $"expected grey, got {b},{g},{r}");
        Assert.Equal(b, g);
        Assert.Equal(g, r);
    }

    [Fact]
    public void RejectsNonSffInput()
    {
        Assert.Null(SffFile.Open(new MemoryStream("not an sff file at all, just text"u8.ToArray())));
    }

    [Fact]
    public void DownscaleFitsAndKeepsAspect()
    {
        var img = new SpriteImage(400, 200, Enumerable.Repeat((byte)255, 400 * 200 * 4).ToArray());
        var small = SpriteImageOps.FitWithin(img, 100, 100);
        Assert.Equal((100, 50), (small.Width, small.Height));
        Assert.Equal(255, small.Bgra[3]);
    }

    [Fact]
    public void ArtworkServiceCachesOutsideRootAndNeverWritesContent()
    {
        using var root = new TestRoot();
        var pal = Palette((0, 0, 0), (200, 100, 50));
        root.Write("chars/kfm/kfm.def", "[Info]\nname = KFM\n[Files]\ncmd = kfm.cmd\ncns = kfm.cns\nsprite = kfm.sff\n");
        root.WriteBytes("chars/kfm/kfm.sff",
            BuildV1([new V1Sprite(9000, 1, 90, 90, Enumerable.Repeat((byte)1, 8100).ToArray(), pal, false)]));
        var before = Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));

        var cacheDir = Path.Combine(Path.GetTempPath(), "ikemenlab-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ThumbnailCache(cacheDir);
            var service = new ArtworkService(root.Path, cache);
            var character = new LibraryIndexService().Index(root.Path).Characters.Single();

            var first = service.CharacterPortraitPng(character);
            Assert.NotNull(first);
            Assert.Single(Directory.GetFiles(cacheDir, "*.png"));

            var second = service.CharacterPortraitPng(character);
            Assert.Equal(first, second);

            var decoded = Png.Decode(first!)!;
            Assert.Equal((90, 90), (decoded.Width, decoded.Height));

            var after = Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));
            Assert.Equal(before, after);

            Assert.Throws<InvalidOperationException>(() =>
                new ArtworkService(root.Path, new ThumbnailCache(Path.Combine(root.Path, "cache"))));
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Fact]
    public void MissesAreRemembered()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "ikemenlab-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ThumbnailCache(cacheDir);
            var calls = 0;
            Assert.Null(cache.GetOrCreate("x", ["nope"], () => { calls++; return null; }));
            Assert.Null(cache.GetOrCreate("x", ["nope"], () => { calls++; return null; }));
            Assert.Equal(1, calls);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }
}
