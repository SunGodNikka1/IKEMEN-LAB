namespace IKEMENLab.Core.Sprites;

public static class SpriteImageOps
{
    /// <summary>Crops to the given rectangle (clamped).</summary>
    public static SpriteImage Crop(SpriteImage src, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, src.Width - 1);
        y = Math.Clamp(y, 0, src.Height - 1);
        width = Math.Clamp(width, 1, src.Width - x);
        height = Math.Clamp(height, 1, src.Height - y);
        var dst = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(src.Bgra, (y + row) * src.Stride + x * 4, dst, row * width * 4, width * 4);
        }

        return new SpriteImage(width, height, dst);
    }

    /// <summary>Crops away fully transparent borders (returns the input when nothing to trim).</summary>
    public static SpriteImage TrimTransparent(SpriteImage src)
    {
        var bounds = src.OpaqueBounds();
        if (bounds is null) return src;
        var (x, y, w, h) = bounds.Value;
        return w == src.Width && h == src.Height ? src : Crop(src, x, y, w, h);
    }

    /// <summary>
    /// Area-average downscale so the image fits inside maxWidth × maxHeight (never upscales).
    /// Colour is averaged with alpha weighting to avoid dark halos around transparent pixels.
    /// </summary>
    public static SpriteImage FitWithin(SpriteImage src, int maxWidth, int maxHeight)
    {
        var scale = Math.Min((double)maxWidth / src.Width, (double)maxHeight / src.Height);
        if (scale >= 1) return src;

        var dw = Math.Max(1, (int)Math.Round(src.Width * scale));
        var dh = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new byte[dw * dh * 4];
        var sx = (double)src.Width / dw;
        var sy = (double)src.Height / dh;

        for (var y = 0; y < dh; y++)
        {
            var y0 = (int)(y * sy);
            var y1 = Math.Max(y0 + 1, (int)Math.Ceiling((y + 1) * sy));
            y1 = Math.Min(y1, src.Height);
            for (var x = 0; x < dw; x++)
            {
                var x0 = (int)(x * sx);
                var x1 = Math.Max(x0 + 1, (int)Math.Ceiling((x + 1) * sx));
                x1 = Math.Min(x1, src.Width);

                double b = 0, g = 0, r = 0, a = 0;
                var n = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    var row = yy * src.Stride;
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var o = row + xx * 4;
                        double pa = src.Bgra[o + 3];
                        b += src.Bgra[o] * pa;
                        g += src.Bgra[o + 1] * pa;
                        r += src.Bgra[o + 2] * pa;
                        a += pa;
                        n++;
                    }
                }

                var d = (y * dw + x) * 4;
                if (a > 0)
                {
                    dst[d] = (byte)Math.Round(b / a);
                    dst[d + 1] = (byte)Math.Round(g / a);
                    dst[d + 2] = (byte)Math.Round(r / a);
                }

                dst[d + 3] = (byte)Math.Round(a / n);
            }
        }

        return new SpriteImage(dw, dh, dst);
    }
}
