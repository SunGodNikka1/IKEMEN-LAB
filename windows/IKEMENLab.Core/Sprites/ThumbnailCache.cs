using System.Security.Cryptography;
using System.Text;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Sprites;

/// <summary>
/// Disk cache of decoded thumbnails, stored as PNG under %LOCALAPPDATA%\IKEMEN Lab\cache\thumbnails.
/// Keys include each source file's full path, size and last-write time plus a decoder version, so an
/// edited SFF is re-decoded automatically. Misses that produced nothing are remembered with a small
/// marker file. The cache refuses to live inside the IKEMEN installation.
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>Bump when decoding/selection output changes so stale thumbnails are ignored.</summary>
    public const int DecoderVersion = 1;

    public ThumbnailCache(string directory)
    {
        Directory = Path.GetFullPath(directory);
    }

    public string Directory { get; }

    public static string DefaultDirectory => Path.Combine(AppDataPaths.GetAppDataDirectory(), "cache", "thumbnails");

    /// <summary>Throws if the cache directory is the IKEMEN root or inside it.</summary>
    public void EnsureOutside(string ikemenRoot)
    {
        var root = Path.GetFullPath(ikemenRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dir = Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Thumbnail cache must not be stored inside the IKEMEN installation.");
        }
    }

    public string KeyFor(string kind, IEnumerable<string?> sourceFiles)
    {
        var sb = new StringBuilder();
        sb.Append(kind).Append('|').Append(DecoderVersion);
        foreach (var file in sourceFiles)
        {
            sb.Append('|');
            if (string.IsNullOrEmpty(file)) continue;
            var full = Path.GetFullPath(file);
            sb.Append(full.ToLowerInvariant());
            try
            {
                var info = new FileInfo(full);
                if (info.Exists) sb.Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>Returns the cached PNG bytes, an empty array for a remembered miss, or null when absent.</summary>
    public byte[]? TryGet(string key)
    {
        var png = PngPath(key);
        try
        {
            if (File.Exists(png)) return File.ReadAllBytes(png);
            if (File.Exists(MissPath(key))) return [];
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    public void Store(string key, SpriteImage? image)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (image is null)
            {
                File.WriteAllBytes(MissPath(key), []);
                return;
            }

            var tmp = PngPath(key) + ".tmp";
            File.WriteAllBytes(tmp, Png.Encode(image));
            File.Move(tmp, PngPath(key), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Cached PNG bytes for the sources, producing and storing them on a miss (null = no image).</summary>
    public byte[]? GetOrCreate(string kind, IReadOnlyList<string?> sources, Func<SpriteImage?> produce)
    {
        var key = KeyFor(kind, sources);
        var cached = TryGet(key);
        if (cached is not null) return cached.Length == 0 ? null : cached;

        var image = produce();
        Store(key, image);
        return image is null ? null : TryGet(key) ?? Png.Encode(image);
    }

    private string PngPath(string key) => Path.Combine(Directory, key + ".png");
    private string MissPath(string key) => Path.Combine(Directory, key + ".none");
}
