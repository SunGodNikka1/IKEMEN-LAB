using System.Security.Cryptography;

namespace IKEMENLab.Core.Mutations;

/// <summary>
/// Validates that mutation targets stay inside a selected IKEMEN root.
/// Rejects traversal, absolute escape, and reparse-point redirection outside the root.
/// </summary>
public static class IkemenPathGuard
{
    public static string NormalizeRoot(string ikemenRoot)
    {
        if (string.IsNullOrWhiteSpace(ikemenRoot))
        {
            throw new ArgumentException("IKEMEN root is required.", nameof(ikemenRoot));
        }

        return Path.GetFullPath(ikemenRoot.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string ResolveUnderRoot(string ikemenRoot, string targetPath)
    {
        var root = NormalizeRoot(ikemenRoot);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path is required.", nameof(targetPath));
        }

        // Reject raw traversal tokens before resolution.
        var segments = targetPath.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            throw new InvalidOperationException("Path traversal ('..') is not allowed.");
        }

        var combined = Path.IsPathRooted(targetPath)
            ? Path.GetFullPath(targetPath)
            : Path.GetFullPath(Path.Combine(root, targetPath));

        EnsureInsideRoot(root, combined);
        EnsureNoReparseEscape(root, combined);
        return combined;
    }

    public static void EnsureInsideRoot(string ikemenRoot, string fullPath)
    {
        var root = NormalizeRoot(ikemenRoot);
        var full = Path.GetFullPath(fullPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!IsUnderRoot(root, full))
        {
            throw new InvalidOperationException(
                $"Target escapes IKEMEN root.{Environment.NewLine}Root: {root}{Environment.NewLine}Target: {full}");
        }
    }

    public static bool IsUnderRoot(string ikemenRoot, string fullPath)
    {
        var root = NormalizeRoot(ikemenRoot) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        if (string.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                NormalizeRoot(ikemenRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            // The root itself is not a valid mutation target.
            return false;
        }

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureDistinctPaths(string source, string target)
    {
        var a = Path.GetFullPath(source)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = Path.GetFullPath(target)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source and target paths must be different.");
        }
    }

    /// <summary>
    /// Walks from root toward the target and rejects any reparse point whose resolved path leaves the root.
    /// Also rejects creating a new leaf through an existing reparse prefix that escapes.
    /// </summary>
    public static void EnsureNoReparseEscape(string ikemenRoot, string fullTarget)
    {
        var root = NormalizeRoot(ikemenRoot);
        var full = Path.GetFullPath(fullTarget);
        EnsureInsideRoot(root, full);

        // Check every existing ancestor of the target, including the target if it exists.
        var current = full;
        while (true)
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                RejectIfReparseEscapes(root, current);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) break;

            var parentNorm = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(parentNorm, root, StringComparison.OrdinalIgnoreCase) ||
                parentNorm.Length < root.Length)
            {
                if (Directory.Exists(root))
                {
                    RejectIfReparseEscapes(root, root);
                }

                break;
            }

            current = parent;
        }
    }

    private static void RejectIfReparseEscapes(string root, string path)
    {
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(path);
        }
        catch
        {
            return;
        }

        if ((attrs & FileAttributes.ReparsePoint) == 0) return;

        // Resolve by asking the OS for the full path after following links where possible.
        string resolved;
        try
        {
            resolved = (attrs & FileAttributes.Directory) != 0
                ? new DirectoryInfo(path).FullName
                : new FileInfo(path).FullName;

            // For junctions, walking into the path yields the target.
            if ((attrs & FileAttributes.Directory) != 0 && Directory.Exists(path))
            {
                var probe = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
                if (probe is not null)
                {
                    resolved = Path.GetFullPath(Path.GetDirectoryName(probe) ?? path);
                }
                else
                {
                    // Empty junction — create a probe via GetFullPath of a child name and check link target via Win32 if needed.
                    resolved = Path.GetFullPath(path);
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to resolve reparse point '{path}': {ex.Message}", ex);
        }

        // Use Windows link target when available (.NET 6+).
        try
        {
            if ((attrs & FileAttributes.Directory) != 0)
            {
                var di = new DirectoryInfo(path);
                if (di.LinkTarget is not null)
                {
                    resolved = Path.GetFullPath(di.LinkTarget);
                }
            }
            else
            {
                var fi = new FileInfo(path);
                if (fi.LinkTarget is not null)
                {
                    resolved = Path.GetFullPath(fi.LinkTarget);
                }
            }
        }
        catch
        {
            // Fall back to resolved above.
        }

        var resolvedNorm = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootWithSep = root + Path.DirectorySeparatorChar;
        var ok = string.Equals(resolvedNorm, root, StringComparison.OrdinalIgnoreCase) ||
                 resolvedNorm.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            throw new InvalidOperationException(
                $"Reparse point escapes IKEMEN root: '{path}' -> '{resolvedNorm}'");
        }
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Sha256DirectoryFingerprint(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(directory, file).Replace('\\', '/');
            hasher.AppendData(System.Text.Encoding.UTF8.GetBytes(rel));
            hasher.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }
}
