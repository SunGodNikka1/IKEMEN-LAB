namespace IKEMENLab.Core.Sprites;

/// <summary>
/// Decoded sprite: top-down rows of 32-bit BGRA with straight (non-premultiplied) alpha.
/// Deliberately UI-agnostic so decoding can be tested without WPF.
/// </summary>
public sealed class SpriteImage
{
    public SpriteImage(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (bgra.Length != width * height * 4) throw new ArgumentException("Pixel buffer size mismatch.", nameof(bgra));
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public int Stride => Width * 4;

    /// <summary>Builds an image from 8-bit indices and a 256-entry palette of 0xAARRGGBB colors.</summary>
    public static SpriteImage FromIndexed(int width, int height, byte[] indices, uint[] palette)
    {
        var bgra = new byte[width * height * 4];
        var count = Math.Min(indices.Length, width * height);
        for (var i = 0; i < count; i++)
        {
            var idx = indices[i];
            var argb = idx < palette.Length ? palette[idx] : 0u;
            var o = i * 4;
            bgra[o] = (byte)argb;
            bgra[o + 1] = (byte)(argb >> 8);
            bgra[o + 2] = (byte)(argb >> 16);
            bgra[o + 3] = (byte)(argb >> 24);
        }

        return new SpriteImage(width, height, bgra);
    }

    /// <summary>Tight bounding box of non-transparent pixels, or null when fully transparent.</summary>
    public (int X, int Y, int Width, int Height)? OpaqueBounds()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (var y = 0; y < Height; y++)
        {
            var row = y * Stride;
            for (var x = 0; x < Width; x++)
            {
                if (Bgra[row + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public int OpaquePixelCount()
    {
        var n = 0;
        for (var i = 3; i < Bgra.Length; i += 4)
        {
            if (Bgra[i] != 0) n++;
        }

        return n;
    }
}
