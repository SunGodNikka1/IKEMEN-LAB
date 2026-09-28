using System.Buffers.Binary;
using System.Text;

namespace IKEMENLab.Core.Sprites;

public sealed class SffSprite
{
    public required int Index { get; init; }
    public required ushort Group { get; init; }
    public required ushort Number { get; init; }

    // v2: from the sprite table. v1: resolved lazily from the PCX header (0 until then).
    internal int Width { get; set; }
    internal int Height { get; set; }

    internal long DataOffset { get; init; }
    internal uint DataLength { get; init; }
    internal ushort Link { get; init; }

    // v1 only
    internal long BlockEnd { get; init; }
    internal bool SamePalette { get; init; }

    // v2 only
    internal byte Format { get; init; }
    internal byte ColorDepth { get; init; }
    internal ushort PaletteIndex { get; init; }

    public override string ToString() => $"{Group},{Number}";
}

/// <summary>
/// Read-only random-access SFF reader (v1 PCX and v2 RLE8/RLE5/LZ5/PNG). Only the sprites that are
/// decoded are read from disk, so large SFFs are cheap to probe. The file is opened read-only with
/// sharing so a running IKEMEN is never disturbed.
/// </summary>
public sealed class SffFile : IDisposable
{
    private const int MaxSprites = 50_000;
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly List<SffSprite> _sprites = [];
    private readonly Dictionary<int, uint[]?> _v1PaletteCache = [];

    // v2 header fields
    private long _paletteListOffset;
    private int _paletteCount;
    private long _ldataOffset;

    private SffFile(Stream stream, bool ownsStream)
    {
        _stream = stream;
        _ownsStream = ownsStream;
    }

    /// <summary>1 or 2.</summary>
    public int Version { get; private set; }

    /// <summary>Second minor version byte (1 for SFF v2.01, whose palettes carry real alpha).</summary>
    public byte VersionLo2 { get; private set; }

    public IReadOnlyList<SffSprite> Sprites => _sprites;

    public static SffFile? Open(string path)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.RandomAccess);
            var file = Open(fs, ownsStream: true);
            if (file is null) fs.Dispose();
            return file;
        }
        catch (IOException)
        {
            fs?.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            fs?.Dispose();
            return null;
        }
    }

    public static SffFile? Open(Stream stream, bool ownsStream = false)
    {
        var file = new SffFile(stream, ownsStream);
        try
        {
            return file.ReadHeader() ? file : null;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }

    public SffSprite? Find(int group, int number)
        => _sprites.FirstOrDefault(s => s.Group == group && s.Number == number);

    /// <summary>SFF v2 palette identified by (group, number), e.g. selectable palette 1,n. Null for v1.</summary>
    public uint[]? PaletteByGroup(int group, int number)
    {
        if (Version != 2) return null;
        Span<byte> node = stackalloc byte[4];
        for (var i = 0; i < _paletteCount; i++)
        {
            if (!ReadAt(_paletteListOffset + i * 16L, node)) return null;
            if (BinaryPrimitives.ReadUInt16LittleEndian(node) == group &&
                BinaryPrimitives.ReadUInt16LittleEndian(node[2..]) == number)
            {
                return V2Palette(i);
            }
        }

        return null;
    }

    /// <summary>Raw 8-bit indices of a sprite (null for true-colour sprites). Used for palette swatches.</summary>
    public (int Width, int Height, byte[] Indices)? DecodeIndices(SffSprite sprite)
    {
        try
        {
            var data = ResolveLinked(sprite);
            if (Version == 1)
            {
                // Decode with a dummy greyscale palette, then read the index back from the blue channel.
                var gray = new uint[256];
                for (var i = 0; i < 256; i++) gray[i] = 0xFF000000u | (uint)i;
                var img = DecodeV1(sprite, null, gray);
                if (img is null) return null;
                var idx = new byte[img.Width * img.Height];
                for (var i = 0; i < idx.Length; i++) idx[i] = img.Bgra[i * 4];
                return (img.Width, img.Height, idx);
            }

            if (data.Format is 2 or 3 or 4 && data.DataLength >= 4)
            {
                var bytes = new byte[data.DataLength];
                if (!ReadAt(data.DataOffset, bytes)) return null;
                var body = bytes.AsSpan(4);
                var indices = data.Format switch
                {
                    2 => SffCodecs.Rle8(body, data.Width, data.Height),
                    3 => SffCodecs.Rle5(body, data.Width, data.Height),
                    _ => SffCodecs.Lz5(body, data.Width, data.Height)
                };
                return (data.Width, data.Height, indices);
            }

            if (data.Format == 10 && data.DataLength > 4)
            {
                var bytes = new byte[data.DataLength];
                if (!ReadAt(data.DataOffset, bytes)) return null;
                var png = Png.TryDecode(bytes.AsSpan(4));
                return png?.Indices is null ? null : (png.Width, png.Height, png.Indices);
            }

            if (data.Format == 0 && data.ColorDepth == 8)
            {
                var bytes = new byte[data.DataLength];
                if (!ReadAt(data.DataOffset, bytes)) return null;
                return (data.Width, data.Height, Pad(bytes, data.Width * data.Height));
            }
        }
        catch (Exception e) when (e is IOException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
        }

        return null;
    }

    /// <summary>The embedded palette a v1 sprite is drawn with (after the previous-sprite chain).</summary>
    public uint[]? EmbeddedPaletteFor(SffSprite sprite)
        => Version == 1 ? V1Palette(PaletteOwner(sprite)) : V2Palette(ResolveLinked(sprite).PaletteIndex);

    /// <summary>Width/height, reading the PCX header for v1 sprites when needed.</summary>
    public (int Width, int Height) Dimensions(SffSprite sprite)
    {
        var data = ResolveLinked(sprite);
        if (Version == 1 && data.Width == 0 && data.DataLength >= 128)
        {
            Span<byte> head = stackalloc byte[12];
            if (ReadAt(data.DataOffset, head))
            {
                var xmin = BinaryPrimitives.ReadUInt16LittleEndian(head[4..]);
                var ymin = BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
                var xmax = BinaryPrimitives.ReadUInt16LittleEndian(head[8..]);
                var ymax = BinaryPrimitives.ReadUInt16LittleEndian(head[10..]);
                data.Width = (ushort)(xmax - xmin + 1);
                data.Height = (ushort)(ymax - ymin + 1);
            }
        }

        return (data.Width, data.Height);
    }

    /// <summary>Byte size of the sprite's stored data (after following links).</summary>
    public long StoredSize(SffSprite sprite) => ResolveLinked(sprite).DataLength;

    /// <summary>
    /// Decodes one sprite. <paramref name="palette0Override"/> replaces the SFF's physical palette 0
    /// the way IKEMEN remaps it to the character's pal1 ACT (v1 only).
    /// </summary>
    public SpriteImage? Decode(SffSprite sprite, uint[]? palette0Override = null)
    {
        try
        {
            return Version == 1 ? DecodeV1(sprite, palette0Override) : DecodeV2(sprite);
        }
        catch (Exception e) when (e is IOException or IndexOutOfRangeException or ArgumentException
                                      or OverflowException or InvalidDataException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ header

    private bool ReadHeader()
    {
        Span<byte> h = stackalloc byte[68];
        if (!ReadAt(0, h[..36])) return false;
        if (!Encoding.ASCII.GetString(h[..11]).Equals("ElecbyteSpr", StringComparison.Ordinal)) return false;

        VersionLo2 = h[13];
        var verHi = h[15];
        if (verHi == 1)
        {
            Version = 1;
            var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(h[20..]), MaxSprites);
            var first = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            ReadV1Subheaders(first, count);
            return true;
        }

        if (verHi == 2)
        {
            Version = 2;
            if (!ReadAt(0, h)) return false;
            var spriteList = BinaryPrimitives.ReadUInt32LittleEndian(h[36..]);
            var spriteCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(h[40..]), MaxSprites);
            _paletteListOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[44..]);
            _paletteCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(h[48..]), 65_535);
            _ldataOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[52..]);
            var tdataOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[60..]);
            ReadV2Table(spriteList, spriteCount, tdataOffset);
            return true;
        }

        return false;
    }

    private void ReadV1Subheaders(uint firstOffset, int count)
    {
        Span<byte> sh = stackalloc byte[19];
        long offset = firstOffset;
        var length = _stream.Length;
        for (var i = 0; i < count; i++)
        {
            if (offset <= 0 || offset + 32 > length) break;
            if (!ReadAt(offset, sh)) break;

            var next = BinaryPrimitives.ReadUInt32LittleEndian(sh);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(sh[4..]);
            var dataOffset = offset + 32;
            var blockEnd = next > offset ? next : dataOffset + size;
            _sprites.Add(new SffSprite
            {
                Index = i,
                Group = BinaryPrimitives.ReadUInt16LittleEndian(sh[12..]),
                Number = BinaryPrimitives.ReadUInt16LittleEndian(sh[14..]),
                Link = BinaryPrimitives.ReadUInt16LittleEndian(sh[16..]),
                SamePalette = sh[18] != 0,
                DataOffset = dataOffset,
                DataLength = size,
                BlockEnd = Math.Min(blockEnd, length)
            });

            if (next <= offset) break;
            offset = next;
        }
    }

    private void ReadV2Table(uint listOffset, int count, uint tdataOffset)
    {
        if (count <= 0 || listOffset + (long)count * 28 > _stream.Length) return;
        var table = new byte[count * 28];
        if (!ReadAt(listOffset, table)) return;

        for (var i = 0; i < count; i++)
        {
            var e = table.AsSpan(i * 28, 28);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(e[26..]);
            var ofs = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
            _sprites.Add(new SffSprite
            {
                Index = i,
                Group = BinaryPrimitives.ReadUInt16LittleEndian(e),
                Number = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]),
                Width = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]),
                Height = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]),
                Link = BinaryPrimitives.ReadUInt16LittleEndian(e[12..]),
                Format = e[14],
                ColorDepth = e[15],
                DataOffset = ((flags & 1) == 0 ? _ldataOffset : tdataOffset) + ofs,
                DataLength = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]),
                PaletteIndex = BinaryPrimitives.ReadUInt16LittleEndian(e[24..])
            });
        }
    }

    // ------------------------------------------------------------------ v1

    private SpriteImage? DecodeV1(SffSprite sprite, uint[]? palette0Override, uint[]? forcedPalette = null)
    {
        var data = ResolveLinked(sprite);
        if (data.DataLength == 0 && data.BlockEnd <= data.DataOffset + 128) return null;

        Span<byte> head = stackalloc byte[128];
        if (!ReadAt(data.DataOffset, head)) return null;
        if (head[3] != 8) return null; // IKEMEN only accepts 8-bit PCX

        var encoding = head[2];
        var width = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(head[8..]) - BinaryPrimitives.ReadUInt16LittleEndian(head[4..]) + 1);
        var height = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(head[10..]) - BinaryPrimitives.ReadUInt16LittleEndian(head[6..]) + 1);
        var bytesPerLine = BinaryPrimitives.ReadUInt16LittleEndian(head[66..]);
        if (width == 0 || height == 0 || width > 16384 || height > 16384) return null;

        var owner = PaletteOwner(sprite);
        var ownPaletteOffset = data.SamePalette && data.Index > 0 ? -1 : FindV1PaletteOffset(data);
        var rleEnd = ownPaletteOffset >= 0 ? ownPaletteOffset : data.BlockEnd;
        var rleStart = data.DataOffset + 128;
        var rleLength = (int)Math.Max(0, rleEnd - rleStart);
        var rle = new byte[rleLength];
        if (rleLength > 0 && !ReadAt(rleStart, rle)) return null;

        var indices = SffCodecs.PcxRle(rle, width, height, encoding == 1 ? bytesPerLine : 0);

        uint[]? palette = forcedPalette
                          ?? (owner == 0 && palette0Override is not null ? palette0Override : V1Palette(owner));
        if (palette is null) return null;
        return SpriteImage.FromIndexed(width, height, indices, palette);
    }

    /// <summary>Index of the sprite whose embedded palette this sprite uses (IKEMEN's prev.palidx chain).</summary>
    private int PaletteOwner(SffSprite sprite)
    {
        // owner(i) = owner(link) for linked sprites, owner(i-1) when "same palette as previous", else i.
        var i = sprite.Index;
        for (var guard = 0; guard < _sprites.Count + 16; guard++)
        {
            var s = _sprites[i];
            if (s.DataLength == 0 && s.Link < _sprites.Count && s.Link != i)
            {
                i = s.Link;
                continue;
            }

            if (s.SamePalette && i > 0)
            {
                i--;
                continue;
            }

            return i;
        }

        return 0;
    }

    private uint[]? V1Palette(int ownerIndex)
    {
        if (_v1PaletteCache.TryGetValue(ownerIndex, out var cached)) return cached;
        uint[]? palette = null;
        var owner = ResolveLinked(_sprites[ownerIndex]);
        var offset = FindV1PaletteOffset(owner);
        if (offset >= 0)
        {
            var rgb = new byte[768];
            if (ReadAt(offset + 1, rgb))
            {
                palette = new uint[256];
                for (var c = 0; c < 256; c++)
                {
                    var a = c == 0 ? 0u : 255u;
                    palette[c] = a << 24 | (uint)rgb[c * 3] << 16 | (uint)rgb[c * 3 + 1] << 8 | rgb[c * 3 + 2];
                }
            }
        }

        _v1PaletteCache[ownerIndex] = palette;
        return palette;
    }

    /// <summary>Position of the 0x0C marker preceding the 768-byte PCX palette (IKEMEN backward scan).</summary>
    private long FindV1PaletteOffset(SffSprite sprite)
    {
        var start = sprite.DataOffset + 128;
        var last = sprite.BlockEnd - 769;
        if (last < start) return -1;

        const int window = 4096;
        var from = Math.Max(start, last - window + 1);
        var buffer = new byte[(int)(last - from + 1)];
        if (!ReadAt(from, buffer)) return -1;
        for (var i = buffer.Length - 1; i >= 0; i--)
        {
            if (buffer[i] == 0x0C) return from + i;
        }

        return last; // IKEMEN falls back to blockEnd - 769
    }

    // ------------------------------------------------------------------ v2

    private SpriteImage? DecodeV2(SffSprite sprite)
    {
        var data = ResolveLinked(sprite);
        int width = data.Width, height = data.Height;
        if (width <= 0 || height <= 0 || data.DataLength == 0) return null;
        if (data.DataOffset + data.DataLength > _stream.Length) return null;

        var bytes = new byte[data.DataLength];
        if (!ReadAt(data.DataOffset, bytes)) return null;

        switch (data.Format)
        {
            case 0:
                if (data.ColorDepth == 8)
                {
                    var pal = V2Palette(data.PaletteIndex);
                    return pal is null ? null : SpriteImage.FromIndexed(width, height, Pad(bytes, width * height), pal);
                }

                return RawTrueColor(bytes, width, height, data.ColorDepth);

            case 2 or 3 or 4:
            {
                if (bytes.Length < 4) return null;
                var body = bytes.AsSpan(4);
                var indices = data.Format switch
                {
                    2 => SffCodecs.Rle8(body, width, height),
                    3 => SffCodecs.Rle5(body, width, height),
                    _ => SffCodecs.Lz5(body, width, height)
                };
                var pal = V2Palette(data.PaletteIndex);
                return pal is null ? null : SpriteImage.FromIndexed(width, height, indices, pal);
            }

            case 10:
            {
                var png = bytes.Length > 4 ? Png.TryDecode(bytes.AsSpan(4)) : null;
                if (png?.Indices is null) return null;
                var pal = V2Palette(data.PaletteIndex);
                return pal is null ? null : SpriteImage.FromIndexed(png.Width, png.Height, png.Indices, pal);
            }

            case 11 or 12:
                return bytes.Length > 4 ? Png.TryDecode(bytes.AsSpan(4))?.ToSpriteImage() : null;

            default:
                return null;
        }
    }

    private uint[]? V2Palette(int index, int depth = 0)
    {
        if (index < 0 || index >= _paletteCount || depth > 8) return null;
        Span<byte> node = stackalloc byte[16];
        if (!ReadAt(_paletteListOffset + index * 16L, node)) return null;
        var link = BinaryPrimitives.ReadUInt16LittleEndian(node[6..]);
        var ofs = BinaryPrimitives.ReadUInt32LittleEndian(node[8..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(node[12..]);
        if (size == 0) return link != index ? V2Palette(link, depth + 1) : null;

        var count = (int)Math.Min(size / 4, 256);
        var rgba = new byte[count * 4];
        if (!ReadAt(_ldataOffset + ofs, rgba)) return null;

        var forcedAlpha = VersionLo2 == 0;
        var pal = new uint[256];
        for (var i = 0; i < count; i++)
        {
            uint a = forcedAlpha ? (i == 0 ? 0u : 255u) : rgba[i * 4 + 3];
            pal[i] = a << 24 | (uint)rgba[i * 4] << 16 | (uint)rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2];
        }

        return pal;
    }

    private static SpriteImage? RawTrueColor(byte[] bytes, int width, int height, int depth)
    {
        var bpp = depth == 24 ? 3 : depth == 32 ? 4 : 0;
        if (bpp == 0 || bytes.Length < width * height * bpp) return null;
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            bgra[i * 4] = bytes[i * bpp + 2];
            bgra[i * 4 + 1] = bytes[i * bpp + 1];
            bgra[i * 4 + 2] = bytes[i * bpp];
            bgra[i * 4 + 3] = bpp == 4 ? bytes[i * bpp + 3] : (byte)255;
        }

        return new SpriteImage(width, height, bgra);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Linked sprites (no stored data) reuse another sprite's data, as in IKEMEN.</summary>
    private SffSprite ResolveLinked(SffSprite sprite)
    {
        var current = sprite;
        for (var hops = 0; hops < 16; hops++)
        {
            if (current.DataLength != 0 || current.Link >= _sprites.Count || current.Link == current.Index) return current;
            current = _sprites[current.Link];
        }

        return current;
    }

    private static byte[] Pad(byte[] data, int size)
    {
        if (data.Length >= size) return data;
        var padded = new byte[size];
        data.CopyTo(padded, 0);
        return padded;
    }

    private bool ReadAt(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > _stream.Length) return false;
        _stream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < buffer.Length)
        {
            var n = _stream.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }

        return true;
    }
}
