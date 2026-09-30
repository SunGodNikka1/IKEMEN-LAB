using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace IKEMENLab.Tests;

/// <summary>Builds tiny but structurally valid SFF v1/v2 files and PNGs for decoder tests.</summary>
internal static class SffTestBuilder
{
    public sealed record V1Sprite(ushort Group, ushort Number, int Width, int Height, byte[]? Indices,
        byte[]? PaletteRgb, bool SamePalette, ushort Link = 0, short AxisX = 0, short AxisY = 0);

    public static byte[] BuildV1(IReadOnlyList<V1Sprite> sprites)
    {
        using var ms = new MemoryStream();
        var header = new byte[512];
        Encoding.ASCII.GetBytes("ElecbyteSpr\0").CopyTo(header, 0);
        header[12] = 0; header[13] = 1; header[14] = 0; header[15] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)sprites.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 32);
        ms.Write(header);

        for (var i = 0; i < sprites.Count; i++)
        {
            var s = sprites[i];
            var pcx = s.Indices is null ? [] : Pcx(s.Width, s.Height, s.Indices, s.SamePalette ? null : s.PaletteRgb);
            var offset = ms.Position;
            var next = i == sprites.Count - 1 ? 0 : offset + 32 + pcx.Length;
            var sub = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(sub, (uint)next);
            BinaryPrimitives.WriteUInt32LittleEndian(sub.AsSpan(4), (uint)pcx.Length);
            BinaryPrimitives.WriteInt16LittleEndian(sub.AsSpan(8), s.AxisX);
            BinaryPrimitives.WriteInt16LittleEndian(sub.AsSpan(10), s.AxisY);
            BinaryPrimitives.WriteUInt16LittleEndian(sub.AsSpan(12), s.Group);
            BinaryPrimitives.WriteUInt16LittleEndian(sub.AsSpan(14), s.Number);
            BinaryPrimitives.WriteUInt16LittleEndian(sub.AsSpan(16), s.Link);
            sub[18] = (byte)(s.SamePalette ? 1 : 0);
            ms.Write(sub);
            ms.Write(pcx);
        }

        return ms.ToArray();
    }

    public static byte[] Pcx(int width, int height, byte[] indices, byte[]? paletteRgb)
    {
        using var ms = new MemoryStream();
        var h = new byte[128];
        h[0] = 10; h[1] = 5; h[2] = 1; h[3] = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), (ushort)(width - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), (ushort)(height - 1));
        h[65] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(66), (ushort)width);
        ms.Write(h);
        foreach (var v in indices)
        {
            if (v >= 0xC0) ms.WriteByte(0xC1);
            ms.WriteByte(v);
        }

        if (paletteRgb is not null)
        {
            ms.WriteByte(0x0C);
            ms.Write(paletteRgb);
        }

        return ms.ToArray();
    }

    public static byte[] Palette(params (byte R, byte G, byte B)[] colors)
    {
        var p = new byte[768];
        for (var i = 0; i < colors.Length; i++)
        {
            p[i * 3] = colors[i].R;
            p[i * 3 + 1] = colors[i].G;
            p[i * 3 + 2] = colors[i].B;
        }

        return p;
    }

    public sealed record V2Sprite(ushort Group, ushort Number, int Width, int Height, byte Format, byte[] Data,
        ushort PaletteIndex = 0, ushort Link = 0, byte ColorDepth = 8, short AxisX = 0, short AxisY = 0);

    /// <param name="palettes">RGBA palettes (4 bytes per colour).</param>
    public static byte[] BuildV2(IReadOnlyList<V2Sprite> sprites, IReadOnlyList<byte[]> palettes, byte versionLo2 = 0)
    {
        var header = new byte[512];
        Encoding.ASCII.GetBytes("ElecbyteSpr\0").CopyTo(header, 0);
        header[12] = 0; header[13] = versionLo2; header[14] = 0; header[15] = 2;

        var palTable = new byte[palettes.Count * 16];
        var sprTable = new byte[sprites.Count * 28];
        using var ldata = new MemoryStream();

        for (var i = 0; i < palettes.Count; i++)
        {
            var node = palTable.AsSpan(i * 16);
            BinaryPrimitives.WriteUInt16LittleEndian(node, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(node[2..], (ushort)(i + 1));
            BinaryPrimitives.WriteUInt16LittleEndian(node[4..], (ushort)(palettes[i].Length / 4));
            BinaryPrimitives.WriteUInt32LittleEndian(node[8..], (uint)ldata.Position);
            BinaryPrimitives.WriteUInt32LittleEndian(node[12..], (uint)palettes[i].Length);
            ldata.Write(palettes[i]);
        }

        for (var i = 0; i < sprites.Count; i++)
        {
            var s = sprites[i];
            var e = sprTable.AsSpan(i * 28);
            BinaryPrimitives.WriteUInt16LittleEndian(e, s.Group);
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], s.Number);
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], (ushort)s.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], (ushort)s.Height);
            BinaryPrimitives.WriteInt16LittleEndian(e[8..], s.AxisX);
            BinaryPrimitives.WriteInt16LittleEndian(e[10..], s.AxisY);
            BinaryPrimitives.WriteUInt16LittleEndian(e[12..], s.Link);
            e[14] = s.Format;
            e[15] = s.ColorDepth;
            BinaryPrimitives.WriteUInt32LittleEndian(e[16..], (uint)ldata.Position);
            BinaryPrimitives.WriteUInt32LittleEndian(e[20..], (uint)s.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(e[24..], s.PaletteIndex);
            ldata.Write(s.Data);
        }

        var palOffset = 512;
        var sprOffset = palOffset + palTable.Length;
        var ldataOffset = sprOffset + sprTable.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), (uint)sprOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)sprites.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), (uint)palOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), (uint)palettes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), (uint)ldataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), (uint)ldata.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), (uint)(ldataOffset + ldata.Length));

        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(palTable);
        ms.Write(sprTable);
        ms.Write(ldata.ToArray());
        return ms.ToArray();
    }

    /// <summary>RLE8 stream with the 4-byte decompressed-length prefix SFF v2 stores.</summary>
    public static byte[] Rle8WithPrefix(byte[] indices)
    {
        using var ms = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)indices.Length);
        ms.Write(len);
        var i = 0;
        while (i < indices.Length)
        {
            var v = indices[i];
            var run = 1;
            while (i + run < indices.Length && indices[i + run] == v && run < 63) run++;
            if (run > 1 || (v & 0xC0) == 0x40)
            {
                ms.WriteByte((byte)(0x40 | run));
                ms.WriteByte(v);
            }
            else
            {
                ms.WriteByte(v);
            }

            i += run;
        }

        return ms.ToArray();
    }

    public static byte[] RgbaPalette(params (byte R, byte G, byte B, byte A)[] colors)
    {
        var p = new byte[colors.Length * 4];
        for (var i = 0; i < colors.Length; i++)
        {
            p[i * 4] = colors[i].R;
            p[i * 4 + 1] = colors[i].G;
            p[i * 4 + 2] = colors[i].B;
            p[i * 4 + 3] = colors[i].A;
        }

        return p;
    }

    /// <summary>8-bit indexed PNG (colour type 3).</summary>
    public static byte[] IndexedPng(int width, int height, byte[] indices, byte[] paletteRgb)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 3;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "PLTE", paletteRgb);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(indices, y * width, width);
            }
        }

        Chunk(ms, "IDAT", raw.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        var t = Encoding.ASCII.GetBytes(type);
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
        s.Write(len);
        s.Write(t);
        s.Write(body);
        var crc = 0xFFFFFFFFu;
        foreach (var b in t.Concat(body))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        var c = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc ^ 0xFFFFFFFFu);
        s.Write(c);
    }

    public static byte[] WithPrefix(byte[] data)
    {
        var result = new byte[data.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)data.Length);
        data.CopyTo(result, 4);
        return result;
    }
}
