namespace IKEMENLab.Core.Sprites;

/// <summary>
/// Pixel codecs ported from IKEMEN GO src/image.go (Rle8Decode, Rle5Decode, Lz5Decode, RlePcxDecode).
/// The loops intentionally mirror the engine, including its clamped read cursor.
/// </summary>
public static class SffCodecs
{
    public static byte[] Rle8(ReadOnlySpan<byte> rle, int width, int height)
    {
        var p = new byte[width * height];
        if (rle.Length == 0) return p;
        int i = 0, j = 0;
        var guard = 0;
        while (j < p.Length)
        {
            var before = j;
            int n = 1;
            var d = rle[i];
            if (i < rle.Length - 1) i++;
            if ((d & 0xC0) == 0x40)
            {
                n = d & 0x3F;
                d = rle[i];
                if (i < rle.Length - 1) i++;
            }

            for (; n > 0; n--)
            {
                if (j < p.Length) p[j++] = d;
            }

            // Truncated input can stall the engine loop (zero-length runs at the clamped cursor).
            if (j == before) { if (++guard > 64) break; }
            else guard = 0;
        }

        return p;
    }

    public static byte[] Rle5(ReadOnlySpan<byte> rle, int width, int height)
    {
        var p = new byte[width * height];
        if (rle.Length == 0) return p;
        int i = 0, j = 0;
        var guard = 0;
        while (j < p.Length)
        {
            int rl = rle[i];
            if (i < rle.Length - 1) i++;
            int dl = rle[i] & 0x7F;
            byte c = 0;
            if (rle[i] >> 7 != 0)
            {
                if (i < rle.Length - 1) i++;
                c = rle[i];
            }

            if (i < rle.Length - 1) i++;
            var before = j;
            while (true)
            {
                if (j < p.Length) p[j++] = c;
                rl--;
                if (rl < 0)
                {
                    dl--;
                    if (dl < 0) break;
                    c = (byte)(rle[i] & 0x1F);
                    rl = rle[i] >> 5;
                    if (i < rle.Length - 1) i++;
                }
            }

            // Truncated input can stall the engine loop; stop instead of spinning.
            if (j == before) { if (++guard > 64) break; }
            else guard = 0;
        }

        return p;
    }

    public static byte[] Lz5(ReadOnlySpan<byte> rle, int width, int height)
    {
        var p = new byte[width * height];
        if (rle.Length == 0) return p;
        int i = 0, j = 0, n;
        byte ct = rle[i];
        int cts = 0;
        byte rb = 0;
        int rbc = 0;
        if (i < rle.Length - 1) i++;
        var guard = 0;

        while (j < p.Length)
        {
            var before = j;
            int d = rle[i];
            if (i < rle.Length - 1) i++;
            if ((ct & (1 << cts)) != 0)
            {
                if ((d & 0x3F) == 0)
                {
                    d = ((d << 2) | rle[i]) + 1;
                    if (i < rle.Length - 1) i++;
                    n = rle[i] + 2;
                    if (i < rle.Length - 1) i++;
                }
                else
                {
                    rb |= (byte)((d & 0xC0) >> rbc);
                    rbc += 2;
                    n = d & 0x3F;
                    if (rbc < 8)
                    {
                        d = rle[i] + 1;
                        if (i < rle.Length - 1) i++;
                    }
                    else
                    {
                        d = rb + 1;
                        rb = 0;
                        rbc = 0;
                    }
                }

                while (true)
                {
                    if (j < p.Length)
                    {
                        p[j] = j - d >= 0 ? p[j - d] : (byte)0;
                        j++;
                    }

                    n--;
                    if (n < 0) break;
                }
            }
            else
            {
                if ((d & 0xE0) == 0)
                {
                    n = rle[i] + 8;
                    if (i < rle.Length - 1) i++;
                }
                else
                {
                    n = d >> 5;
                    d &= 0x1F;
                }

                for (; n > 0; n--)
                {
                    if (j < p.Length) p[j++] = (byte)d;
                }
            }

            cts++;
            if (cts >= 8)
            {
                ct = rle[i];
                cts = 0;
                if (i < rle.Length - 1) i++;
            }

            if (j == before) { if (++guard > 64) break; }
            else guard = 0;
        }

        return p;
    }

    /// <summary>PCX run-length decode honouring bytes-per-line padding (IKEMEN RlePcxDecode).</summary>
    public static byte[] PcxRle(ReadOnlySpan<byte> rle, int width, int height, int bytesPerLine)
    {
        var p = new byte[width * height];
        if (rle.Length == 0) return p;
        if (bytesPerLine <= 0)
        {
            rle[..Math.Min(rle.Length, p.Length)].CopyTo(p);
            return p;
        }

        int i = 0, j = 0, k = 0;
        var guard = 0;
        while (j < p.Length)
        {
            var before = j;
            int n = 1;
            var d = rle[i];
            if (i < rle.Length - 1) i++;
            if (d >= 0xC0)
            {
                n = d & 0x3F;
                d = rle[i];
                if (i < rle.Length - 1) i++;
            }

            for (; n > 0; n--)
            {
                if (k < width && j < p.Length) p[j++] = d;
                k++;
                if (k == bytesPerLine)
                {
                    k = 0;
                    n = 1;
                }
            }

            if (j == before && ++guard > bytesPerLine * 4 + 64) break;
            if (j != before) guard = 0;
        }

        return p;
    }
}
