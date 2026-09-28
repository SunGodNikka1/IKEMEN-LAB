namespace IKEMENLab.Core.Sprites;

public enum PortraitSource
{
    /// <summary>A PNG shipped next to the character (portrait.png / *select*.png).</summary>
    PngFile,

    /// <summary>SFF group 9000 (the select-screen portrait).</summary>
    Group9000,

    /// <summary>No portrait group; the 0,0 standing sprite is shown instead.</summary>
    StandingSprite
}

public sealed record ExtractedSprite(SpriteImage Image, PortraitSource Source, int Group, int Number);

/// <summary>
/// Chooses and decodes representative sprites. Selection follows the macOS app (prefer a
/// normal-sized 9000,1 over 9000,2 over 9000,0) with a standing-sprite fallback for both SFF versions.
/// </summary>
public static class SpriteExtractor
{
    public static ExtractedSprite? ExtractPortrait(string sffPath, uint[]? pal1 = null)
    {
        using var sff = SffFile.Open(sffPath);
        return sff is null ? null : ExtractPortrait(sff, pal1);
    }

    public static ExtractedSprite? ExtractPortrait(SffFile sff, uint[]? pal1 = null)
    {
        (SffSprite Sprite, SpriteImage Image)? Decode(int number)
        {
            var sprite = sff.Find(9000, number);
            if (sprite is null) return null;
            var image = sff.Decode(sprite, pal1);
            return image is null || image.OpaquePixelCount() == 0 ? null : (sprite, image);
        }

        static bool GoodSize(SpriteImage i) => i.Width is >= 80 and <= 250 && i.Height is >= 80 and <= 250;

        var d1 = Decode(1);
        var d2 = Decode(2);
        var d0 = Decode(0);

        foreach (var candidate in new[] { d1, d2 })
        {
            if (candidate is { } c && GoodSize(c.Image)) return Wrap(c, PortraitSource.Group9000);
        }

        foreach (var candidate in new[] { d1, d2, d0 })
        {
            if (candidate is { } c) return Wrap(c, PortraitSource.Group9000);
        }

        var standing = sff.Find(0, 0);
        if (standing is not null)
        {
            var image = sff.Decode(standing, pal1);
            if (image is not null && image.OpaquePixelCount() > 0)
            {
                return new ExtractedSprite(SpriteImageOps.TrimTransparent(image), PortraitSource.StandingSprite, 0, 0);
            }
        }

        return null;

        static ExtractedSprite Wrap((SffSprite Sprite, SpriteImage Image) c, PortraitSource source)
            => new(c.Image, source, c.Sprite.Group, c.Sprite.Number);
    }

    /// <summary>
    /// Stage thumbnail: group 9000 if the author provided one, else the largest sprite (the
    /// background layer), else 0,0.
    /// </summary>
    public static ExtractedSprite? ExtractStagePreview(string sffPath)
    {
        using var sff = SffFile.Open(sffPath);
        return sff is null ? null : ExtractStagePreview(sff);
    }

    public static ExtractedSprite? ExtractStagePreview(SffFile sff)
    {
        var preview = sff.Sprites.FirstOrDefault(s => s.Group == 9000);
        if (preview is not null && sff.Decode(preview) is { } img && img.OpaquePixelCount() > 0)
        {
            return new ExtractedSprite(img, PortraitSource.Group9000, preview.Group, preview.Number);
        }

        // Rank by pixel area (v2 table / v1 PCX header); fall back to stored size.
        var ranked = sff.Sprites
            .Take(4000)
            .Select(s =>
            {
                var (w, h) = sff.Dimensions(s);
                return (Sprite: s, Area: (long)w * h, Size: sff.StoredSize(s));
            })
            .Where(x => x.Area > 0 || x.Size > 0)
            .OrderByDescending(x => x.Area)
            .ThenByDescending(x => x.Size)
            .Take(6)
            .ToList();

        // Among the biggest sprites, prefer the one with the most visible (opaque, non-black) pixels so
        // full-screen light/glow overlays lose to the actual background art.
        (SffSprite Sprite, SpriteImage Image, double Score)? best = null;
        foreach (var (sprite, _, _) in ranked)
        {
            var image = sff.Decode(sprite);
            if (image is null || image.OpaquePixelCount() <= image.Width * image.Height / 8) continue;
            image = ComposeChannelSplit(sff, sprite, image) ?? image;
            var score = (double)image.Width * image.Height * VisibleFraction(image) * Richness(image);
            if (best is null || score > best.Value.Score) best = (sprite, image, score);
        }

        if (best is { } b)
        {
            return new ExtractedSprite(b.Image, PortraitSource.Group9000, b.Sprite.Group, b.Sprite.Number);
        }

        var first = sff.Find(0, 0);
        if (first is not null && sff.Decode(first) is { } fallback)
        {
            return new ExtractedSprite(fallback, PortraitSource.Group9000, 0, 0);
        }

        return null;
    }

    /// <summary>
    /// Some SFF v1 stages beat the 256-colour limit by storing one background as three same-sized
    /// single-channel layers (blue, green, red) drawn with additive blending. When the chosen sprite is
    /// such a layer, add its sibling channels so the preview matches what IKEMEN renders.
    /// </summary>
    internal static SpriteImage? ComposeChannelSplit(SffFile sff, SffSprite chosen, SpriteImage image)
    {
        var channel = DominantChannel(image);
        if (channel < 0) return null;

        var (w, h) = (image.Width, image.Height);
        var found = new Dictionary<int, SpriteImage> { [channel] = image };
        foreach (var sibling in sff.Sprites)
        {
            if (found.Count == 3) break;
            if (sibling.Index == chosen.Index || sibling.Group != chosen.Group) continue;
            if (sff.Dimensions(sibling) != (w, h)) continue;
            var decoded = sff.Decode(sibling);
            if (decoded is null) continue;
            var c = DominantChannel(decoded);
            if (c >= 0 && !found.ContainsKey(c)) found[c] = decoded;
        }

        if (found.Count < 2) return null;

        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var o = i * 4;
            int b = 0, g = 0, r = 0, a = 0;
            foreach (var layer in found.Values)
            {
                b += layer.Bgra[o];
                g += layer.Bgra[o + 1];
                r += layer.Bgra[o + 2];
                a = Math.Max(a, layer.Bgra[o + 3]);
            }

            bgra[o] = (byte)Math.Min(255, b);
            bgra[o + 1] = (byte)Math.Min(255, g);
            bgra[o + 2] = (byte)Math.Min(255, r);
            bgra[o + 3] = (byte)a;
        }

        return new SpriteImage(w, h, bgra);
    }

    /// <summary>Share of sampled pixels that are opaque and not near-black.</summary>
    internal static double VisibleFraction(SpriteImage image)
    {
        long total = 0, visible = 0;
        var step = Math.Max(1, image.Width * image.Height / 20000) * 4;
        for (var o = 0; o + 3 < image.Bgra.Length; o += step)
        {
            total++;
            if (image.Bgra[o + 3] != 0 && image.Bgra[o] + image.Bgra[o + 1] + image.Bgra[o + 2] > 48) visible++;
        }

        return total == 0 ? 0 : (double)visible / total;
    }

    /// <summary>
    /// 0..1 colour richness from the number of distinct (quantised) colours: flat fills, two-tone masks
    /// and checkerboards score near zero, painted backgrounds score 1.
    /// </summary>
    internal static double Richness(SpriteImage image)
    {
        var seen = new HashSet<int>();
        var step = Math.Max(1, image.Width * image.Height / 20000) * 4;
        for (var o = 0; o + 3 < image.Bgra.Length && seen.Count < 64; o += step)
        {
            if (image.Bgra[o + 3] == 0) continue;
            seen.Add((image.Bgra[o] >> 4) | (image.Bgra[o + 1] >> 4) << 4 | (image.Bgra[o + 2] >> 4) << 8);
        }

        return Math.Min(1.0, seen.Count / 48.0);
    }

    /// <summary>0/1/2 (B/G/R) when nearly every opaque pixel carries only that channel, else -1.</summary>
    internal static int DominantChannel(SpriteImage image)
    {
        Span<long> hits = stackalloc long[3];
        long opaque = 0, mixed = 0;
        var step = Math.Max(1, image.Width * image.Height / 20000) * 4;
        for (var o = 0; o + 3 < image.Bgra.Length; o += step)
        {
            if (image.Bgra[o + 3] == 0) continue;
            int b = image.Bgra[o], g = image.Bgra[o + 1], r = image.Bgra[o + 2];
            if (b + g + r < 24) continue; // near-black says nothing about the channel
            opaque++;
            var max = Math.Max(b, Math.Max(g, r));
            var others = b + g + r - max;
            if (others > 16) { mixed++; continue; }
            hits[max == b ? 0 : max == g ? 1 : 2]++;
        }

        if (opaque < 64 || mixed > opaque / 50) return -1;
        for (var c = 0; c < 3; c++)
        {
            if (hits[c] >= opaque * 98 / 100) return c;
        }

        return -1;
    }

    /// <summary>
    /// Character portrait lookup (macOS order): portrait.png, a PNG whose name contains
    /// "portrait" or "select", then the DEF's sprite file, then an SFF named like the DEF or folder.
    /// </summary>
    public static ExtractedSprite? ExtractCharacterPortrait(
        string characterFolder, string defFullPath, string? spriteFile, string? pal1Path)
    {
        foreach (var png in CandidatePngs(characterFolder))
        {
            try
            {
                var decoded = Png.TryDecode(File.ReadAllBytes(png));
                if (decoded is not null) return new ExtractedSprite(decoded.ToSpriteImage(), PortraitSource.PngFile, -1, -1);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var pal1 = pal1Path is not null && File.Exists(pal1Path) ? ActPalette.Read(pal1Path) : null;
        if (spriteFile is not null && File.Exists(spriteFile))
        {
            return ExtractPortrait(spriteFile, pal1);
        }

        // DEF sprite reference missing: try an SFF named like the DEF or folder, then any SFF.
        var fallback = FallbackSff(characterFolder, defFullPath);
        return fallback is null ? null : ExtractPortrait(fallback, pal1);
    }

    private static string? FallbackSff(string folder, string defFullPath)
    {
        try
        {
            var sffs = Directory.EnumerateFiles(folder, "*.sff")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var defName = Path.GetFileNameWithoutExtension(defFullPath);
            var dirName = Path.GetFileName(folder);
            return sffs.FirstOrDefault(f =>
                       string.Equals(Path.GetFileNameWithoutExtension(f), defName, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(Path.GetFileNameWithoutExtension(f), dirName, StringComparison.OrdinalIgnoreCase))
                   ?? sffs.FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> CandidatePngs(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        var portrait = Path.Combine(folder, "portrait.png");
        if (File.Exists(portrait)) yield return portrait;

        IEnumerable<string> pngs;
        try
        {
            pngs = Directory.EnumerateFiles(folder, "*.png")
                .Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return name != "portrait" && (name.Contains("portrait") || name.Contains("select"));
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var png in pngs) yield return png;
    }
}
