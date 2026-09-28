using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Install;

/// <summary>
/// Extracts zip/rar/7z into AppData staging. Never extracts into the live IKEMEN root.
/// Rejects ACE, path traversal, absolute entries, and escape outside staging.
/// </summary>
public static class ArchiveExtractor
{
    public const int MaxArchiveEntries = 50_000;
    public const long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024; // 2 GiB soft ceiling

    public static string GetDefaultStagingRoot()
        => Path.Combine(AppDataPaths.GetAppDataDirectory(), "install-staging");

    public static bool IsArchiveFile(string path)
    {
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".zip" or ".rar" or ".7z") return true;
        return DetectFormat(path) is "zip" or "rar" or "7z";
    }

    /// <summary>Returns zip|rar|7z|ace|null based on magic bytes.</summary>
    public static string? DetectFormat(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[14];
            var read = fs.Read(header);
            if (read < 2) return null;

            if (header[0] == 0x50 && header[1] == 0x4B) return "zip";
            if (read >= 4 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21)
                return "rar";
            if (read >= 6 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC && header[3] == 0xAF)
                return "7z";
            if (read >= 14 &&
                header[7] == 0x2A && header[8] == 0x2A && header[9] == 0x41 &&
                header[10] == 0x43 && header[11] == 0x45 && header[12] == 0x2A && header[13] == 0x2A)
                return "ace";
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Extracts archive into a new unique directory under stagingRoot.
    /// Returns the staging directory path. Caller owns cleanup.
    /// </summary>
    public static string ExtractToStaging(string archivePath, string? stagingRoot = null)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive not found.", archivePath);

        var format = DetectFormat(archivePath) ?? Path.GetExtension(archivePath).TrimStart('.').ToLowerInvariant();
        if (format == "ace")
            throw new InvalidOperationException("ACE archives are not supported.");
        if (format is not ("zip" or "rar" or "7z"))
            throw new InvalidOperationException($"Unsupported archive format: {format}. Supported: zip, rar, 7z.");

        var root = stagingRoot ?? GetDefaultStagingRoot();
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            ExtractSafe(archivePath, staging);
            return staging;
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    public static void ExtractSafe(string archivePath, string stagingDirectory)
    {
        var stagingFull = Path.GetFullPath(stagingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(stagingFull);

        long totalBytes = 0;
        var entryCount = 0;

        using var stream = File.OpenRead(archivePath);
        using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(stream);

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;

            entryCount++;
            if (entryCount > MaxArchiveEntries)
                throw new InvalidOperationException("Archive has too many entries.");

            var relative = NormalizeArchiveEntryPath(entry.Key);
            var destination = ResolveUnderStaging(stagingFull, relative);

            if (entry.Size > 0)
            {
                totalBytes += entry.Size;
                if (totalBytes > MaxTotalUncompressedBytes)
                    throw new InvalidOperationException("Archive uncompressed size exceeds safety limit.");
            }

            // Reject symlink / hard-link style entries when the library exposes them.
            var linkTarget = entry.LinkTarget;
            if (!string.IsNullOrEmpty(linkTarget))
                throw new InvalidOperationException($"Archive entry is a link and was rejected: {entry.Key}");

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            using var entryStream = entry.OpenEntryStream();
            using var outStream = File.Create(destination);
            entryStream.CopyTo(outStream);

            // Ensure written file did not escape (junction tricks mid-write are rare; re-check path).
            var written = Path.GetFullPath(destination);
            if (!IsUnderDirectory(stagingFull, written))
                throw new InvalidOperationException($"Extracted file escaped staging: {entry.Key}");
        }
    }

    /// <summary>
    /// Normalizes and validates an archive entry key. Rejects absolute, drive-qualified, and traversal paths.
    /// </summary>
    public static string NormalizeArchiveEntryPath(string? entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey))
            throw new InvalidOperationException("Archive entry has an empty path.");

        var key = entryKey.Replace('\\', '/').Trim();

        // Absolute / UNC / drive-qualified
        if (key.StartsWith('/') || key.StartsWith('\\'))
            throw new InvalidOperationException($"Absolute archive path rejected: {entryKey}");
        if (key.Length >= 2 && key[1] == ':')
            throw new InvalidOperationException($"Drive-qualified archive path rejected: {entryKey}");
        if (key.StartsWith("//", StringComparison.Ordinal) || key.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"UNC archive path rejected: {entryKey}");

        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new InvalidOperationException($"Malformed archive entry path: {entryKey}");

        foreach (var part in parts)
        {
            if (part is "." or "..")
                throw new InvalidOperationException($"Path traversal in archive entry rejected: {entryKey}");
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException($"Invalid characters in archive entry: {entryKey}");
        }

        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    public static string ResolveUnderStaging(string stagingFull, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(stagingFull, relativePath));
        if (!IsUnderDirectory(stagingFull, combined))
            throw new InvalidOperationException($"Archive entry escapes staging: {relativePath}");
        return combined;
    }

    public static bool IsUnderDirectory(string parentDir, string childPath)
    {
        var parent = Path.GetFullPath(parentDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var child = Path.GetFullPath(childPath);
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
