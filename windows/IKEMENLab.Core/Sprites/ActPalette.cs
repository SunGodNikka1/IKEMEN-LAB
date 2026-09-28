namespace IKEMENLab.Core.Sprites;

/// <summary>
/// MUGEN .act palettes. Colours are stored in reverse order (file entry 0 is palette index 255),
/// exactly as IKEMEN's readActPalette assigns them. Index 0 is transparent.
/// </summary>
public static class ActPalette
{
    public static uint[]? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = new byte[768];
            var n = 0;
            while (n < data.Length)
            {
                var r = fs.Read(data, n, data.Length - n);
                if (r <= 0) break;
                n += r;
            }

            return n < 3 ? null : Parse(data.AsSpan(0, n));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static uint[] Parse(ReadOnlySpan<byte> data)
    {
        var pal = new uint[256];
        var count = Math.Min(data.Length / 3, 256);
        for (var i = 0; i < count; i++)
        {
            var dest = 255 - i;
            var a = dest == 0 ? 0u : 255u;
            pal[dest] = a << 24 | (uint)data[i * 3] << 16 | (uint)data[i * 3 + 1] << 8 | data[i * 3 + 2];
        }

        return pal;
    }

    /// <summary>Representative swatch colours (opaque RGB) for the palette strip in the inspector.</summary>
    public static IReadOnlyList<uint> Swatches(uint[] palette, int count = 6)
    {
        // Skip the transparent slot; sample evenly across the ramps artists usually lay out.
        var result = new List<uint>(count);
        for (var k = 0; k < count; k++)
        {
            var idx = 1 + (int)Math.Round(k * (254.0 / Math.Max(1, count - 1)));
            result.Add(palette[Math.Clamp(idx, 1, 255)] | 0xFF000000u);
        }

        return result;
    }
}
