using System.Buffers.Binary;
using System.IO.Compression;

namespace IKEMENLab.Core.Sprites;

/// <summary>Decoded PNG. Indexed images keep their raw indices so an SFF palette can be applied.</summary>
public sealed class PngImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>BGRA straight alpha, top-down.</summary>
    public required byte[] Bgra { get; init; }

    /// <summary>Per-pixel palette indices for colour type 3 (null otherwise).</summary>
    public byte[]? Indices { get; init; }

    public SpriteImage ToSpriteImage() => new(Width, Height, Bgra);
}

/// <summary>
/// Small dependency-free PNG codec: decodes colour types 0/2/3/4/6 at bit depths 1–16,
/// including Adam7 interlacing, and encodes 32-bit RGBA. Enough for SFF v2 sprites,
/// character portrait PNGs and the thumbnail cache.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool HasSignature(ReadOnlySpan<byte> data)
        => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public static PngImage? TryDecode(ReadOnlySpan<byte> data)
    {
        try
        {
            return Decode(data);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException
                                      or OverflowException or IOException)
        {
            return null;
        }
    }

    public static PngImage? Decode(ReadOnlySpan<byte> data)
    {
        if (!HasSignature(data)) return null;

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? plte = null;
        byte[]? trns = null;
        using var idat = new MemoryStream();

        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            if (length < 0 || pos + 12 + length > data.Length) break;
            var type = data.Slice(pos + 4, 4);
            var body = data.Slice(pos + 8, length);

            if (type.SequenceEqual("IHDR"u8))
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                bitDepth = body[8];
                colorType = body[9];
                interlace = body[12];
            }
            else if (type.SequenceEqual("PLTE"u8)) plte = body.ToArray();
            else if (type.SequenceEqual("tRNS"u8)) trns = body.ToArray();
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(body);
            else if (type.SequenceEqual("IEND"u8)) break;

            pos += 12 + length;
        }

        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || bitDepth is not (1 or 2 or 4 or 8 or 16)) return null;

        idat.Position = 0;
        byte[] raw;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        using (var outStream = new MemoryStream())
        {
            z.CopyTo(outStream);
            raw = outStream.ToArray();
        }

        var bitsPerPixel = channels * bitDepth;
        var bgra = new byte[width * height * 4];
        var indices = colorType == 3 ? new byte[width * height] : null;
        var palette = BuildPalette(colorType, plte, trns);

        var rawPos = 0;
        if (interlace == 0)
        {
            DecodePass(raw, ref rawPos, width, height, bitsPerPixel, bitDepth, colorType, trns, palette,
                bgra, indices, width, 0, 0, 1, 1);
        }
        else
        {
            int[] xs = [0, 4, 0, 2, 0, 1, 0], ys = [0, 0, 4, 0, 2, 0, 1];
            int[] dx = [8, 8, 4, 4, 2, 2, 1], dy = [8, 8, 8, 4, 4, 2, 2];
            for (var p = 0; p < 7; p++)
            {
                var pw = (width - xs[p] + dx[p] - 1) / dx[p];
                var ph = (height - ys[p] + dy[p] - 1) / dy[p];
                if (pw <= 0 || ph <= 0) continue;
                DecodePass(raw, ref rawPos, pw, ph, bitsPerPixel, bitDepth, colorType, trns, palette,
                    bgra, indices, width, xs[p], ys[p], dx[p], dy[p]);
            }
        }

        return new PngImage { Width = width, Height = height, Bgra = bgra, Indices = indices };
    }

    private static uint[]? BuildPalette(int colorType, byte[]? plte, byte[]? trns)
    {
        if (colorType != 3 || plte is null) return null;
        var pal = new uint[256];
        for (var i = 0; i < 256 && i * 3 + 2 < plte.Length; i++)
        {
            var a = trns is not null && i < trns.Length ? trns[i] : (byte)255;
            pal[i] = (uint)(a << 24 | plte[i * 3] << 16 | plte[i * 3 + 1] << 8 | plte[i * 3 + 2]);
        }

        return pal;
    }

    private static void DecodePass(
        byte[] raw, ref int rawPos, int pw, int ph, int bitsPerPixel, int bitDepth, int colorType,
        byte[]? trns, uint[]? palette, byte[] bgra, byte[]? indices, int fullWidth,
        int x0, int y0, int dx, int dy)
    {
        var stride = (pw * bitsPerPixel + 7) / 8;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var prev = new byte[stride];
        var cur = new byte[stride];

        for (var y = 0; y < ph; y++)
        {
            if (rawPos + 1 + stride > raw.Length) return;
            var filter = raw[rawPos];
            Buffer.BlockCopy(raw, rawPos + 1, cur, 0, stride);
            rawPos += 1 + stride;
            Unfilter(filter, cur, prev, bpp);

            for (var x = 0; x < pw; x++)
            {
                var dst = ((y0 + y * dy) * fullWidth + x0 + x * dx);
                WritePixel(cur, x, bitDepth, colorType, trns, palette, bgra, indices, dst);
            }

            (prev, cur) = (cur, prev);
        }
    }

    private static void Unfilter(byte filter, byte[] cur, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0: return;
            case 1:
                for (var i = bpp; i < cur.Length; i++) cur[i] += cur[i - bpp];
                return;
            case 2:
                for (var i = 0; i < cur.Length; i++) cur[i] += prev[i];
                return;
            case 3:
                for (var i = 0; i < cur.Length; i++)
                {
                    var left = i >= bpp ? cur[i - bpp] : 0;
                    cur[i] += (byte)((left + prev[i]) >> 1);
                }

                return;
            case 4:
                for (var i = 0; i < cur.Length; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    cur[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }

                return;
            default:
                throw new InvalidDataException("Unknown PNG filter " + filter);
        }
    }

    private static int Sample(byte[] row, int index, int bitDepth)
    {
        switch (bitDepth)
        {
            case 8: return row[index];
            case 16: return row[index * 2]; // keep the high byte
            default:
                var perByte = 8 / bitDepth;
                var b = row[index / perByte];
                var shift = 8 - bitDepth * (index % perByte + 1);
                return (b >> shift) & ((1 << bitDepth) - 1);
        }
    }

    private static int Scale(int v, int bitDepth) => bitDepth switch
    {
        1 => v * 255,
        2 => v * 85,
        4 => v * 17,
        _ => v
    };

    private static void WritePixel(
        byte[] row, int x, int bitDepth, int colorType, byte[]? trns, uint[]? palette,
        byte[] bgra, byte[]? indices, int dstPixel)
    {
        int r, g, b, a = 255;
        switch (colorType)
        {
            case 0:
            {
                var raw = Sample(row, x, bitDepth);
                r = g = b = Scale(raw, bitDepth);
                if (trns is { Length: >= 2 })
                {
                    // tRNS holds a 16-bit big-endian sample; 16-bit images are compared on the high byte.
                    var key = bitDepth == 16 ? trns[0] : trns[0] << 8 | trns[1];
                    if (raw == key) a = 0;
                }
                break;
            }
            case 2:
                r = Sample(row, x * 3, bitDepth);
                g = Sample(row, x * 3 + 1, bitDepth);
                b = Sample(row, x * 3 + 2, bitDepth);
                if (trns is { Length: >= 6 } && bitDepth == 8 && r == trns[1] && g == trns[3] && b == trns[5]) a = 0;
                break;
            case 3:
            {
                var idx = Sample(row, x, bitDepth);
                if (indices is not null) indices[dstPixel] = (byte)idx;
                var argb = palette is not null ? palette[idx] : 0xFF000000u | (uint)(idx * 0x010101);
                bgra[dstPixel * 4] = (byte)argb;
                bgra[dstPixel * 4 + 1] = (byte)(argb >> 8);
                bgra[dstPixel * 4 + 2] = (byte)(argb >> 16);
                bgra[dstPixel * 4 + 3] = (byte)(argb >> 24);
                return;
            }
            case 4:
                r = g = b = Sample(row, x * 2, bitDepth);
                a = Sample(row, x * 2 + 1, bitDepth);
                break;
            default: // 6
                r = Sample(row, x * 4, bitDepth);
                g = Sample(row, x * 4 + 1, bitDepth);
                b = Sample(row, x * 4 + 2, bitDepth);
                a = Sample(row, x * 4 + 3, bitDepth);
                break;
        }

        var o = dstPixel * 4;
        bgra[o] = (byte)b;
        bgra[o + 1] = (byte)g;
        bgra[o + 2] = (byte)r;
        bgra[o + 3] = (byte)a;
    }

    // ------------------------------------------------------------------ encoder

    /// <summary>Encodes a 32-bit RGBA PNG (non-interlaced, filter 0).</summary>
    public static byte[] Encode(SpriteImage image)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)image.Height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(ms, "IHDR"u8, ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var line = new byte[1 + image.Width * 4];
            for (var y = 0; y < image.Height; y++)
            {
                line[0] = 0;
                var src = y * image.Stride;
                for (var x = 0; x < image.Width; x++)
                {
                    var s = src + x * 4;
                    var d = 1 + x * 4;
                    line[d] = image.Bgra[s + 2];
                    line[d + 1] = image.Bgra[s + 1];
                    line[d + 2] = image.Bgra[s];
                    line[d + 3] = image.Bgra[s + 3];
                }

                z.Write(line);
            }
        }

        WriteChunk(ms, "IDAT"u8, raw.ToArray());
        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
        s.Write(len);
        s.Write(type);
        s.Write(body);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, type), body) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }

            return t;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
