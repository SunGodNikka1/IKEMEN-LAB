using System.Security.Cryptography;

namespace IKEMENLab.Core.Mutations;

public enum MutationFailureKind
{
    Other,
    InUse,
    AccessDenied,
    DiskFull,
    Conflict,
    VerificationFailed,
    UnsafeTarget
}

/// <summary>A mutation failure with a user-facing message plus the raw technical cause.</summary>
public sealed class SafeMutationException : IOException
{
    public SafeMutationException(MutationFailureKind kind, string message, string? technicalDetail = null,
        int? win32Error = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        TechnicalDetail = technicalDetail;
        Win32Error = win32Error;
    }

    public MutationFailureKind Kind { get; }
    public string? TechnicalDetail { get; }
    public int? Win32Error { get; }
}

/// <summary>
/// Same-volume file replacement for IKEMEN files.
///
/// Win32 ReplaceFile renames the replaced file to the backup name, which cannot cross volumes;
/// passing a backup under %LOCALAPPDATA% (C:) for an IKEMEN install on another drive fails with
/// ERROR_UNABLE_TO_REMOVE_REPLACED (1175). The app keeps its own verified backup elsewhere, so the
/// swap here never uses a ReplaceFile backup: the staging file must sit next to the target, and the
/// swap is ReplaceFile(staging → target) with a MoveFileEx(REPLACE_EXISTING) fallback only for
/// filesystems that do not implement ReplaceFile.
/// </summary>
public static class FileReplacer
{
    public const int ErrorInvalidFunction = 1;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNotSameDevice = 17;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    public const int ErrorHandleDiskFull = 39;
    public const int ErrorNotSupported = 50;
    public const int ErrorDiskFull = 112;
    public const int ErrorUnableToRemoveReplaced = 1175;
    public const int ErrorUnableToMoveReplacement = 1176;
    public const int ErrorUnableToMoveReplacement2 = 1177;

    /// <summary>Delays between attempts while another process briefly holds the target (AV scan, indexer, editor save).</summary>
    public static IReadOnlyList<TimeSpan> RetryDelays { get; set; } =
        [TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600)];

    /// <summary>
    /// Atomically swaps <paramref name="staging"/> into <paramref name="target"/> (same directory).
    /// Returns the method used ("ReplaceFile" or "MoveFileEx"). On failure the target keeps its
    /// original content and a classified <see cref="SafeMutationException"/> is thrown.
    /// </summary>
    public static string Replace(string staging, string target)
    {
        EnsureSameVolume(staging, target);
        var attempt = 0;
        while (true)
        {
            try
            {
                File.Replace(staging, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return "ReplaceFile";
            }
            catch (Exception ex) when (IsUnsupported(ex))
            {
                return MoveReplace(staging, target);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < RetryDelays.Count)
            {
                Thread.Sleep(RetryDelays[attempt++]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Classify(ex, target);
            }
        }
    }

    private static string MoveReplace(string staging, string target)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                File.Move(staging, target, overwrite: true);
                return "MoveFileEx";
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < RetryDelays.Count)
            {
                Thread.Sleep(RetryDelays[attempt++]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Classify(ex, target);
            }
        }
    }

    public static int? Win32Error(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return ErrorAccessDenied;
        var hr = ex.HResult;
        return (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? hr & 0xFFFF : null;
    }

    /// <summary>
    /// With no ReplaceFile backup, 1175 means the target could not be renamed away — in practice
    /// another process has it open without delete sharing. Sharing/lock violations are the same.
    /// </summary>
    public static bool IsTransient(Exception ex)
        => Win32Error(ex) is ErrorSharingViolation or ErrorLockViolation or ErrorUnableToRemoveReplaced;

    private static bool IsUnsupported(Exception ex)
        => ex is PlatformNotSupportedException or NotSupportedException ||
           Win32Error(ex) is ErrorInvalidFunction or ErrorNotSupported;

    public const string FileUnchanged = "The file was not changed.";
    public const string FolderUnchanged = "The folder was not changed.";

    public static SafeMutationException Classify(Exception ex, string path, bool isDirectory = false)
    {
        if (ex is SafeMutationException sme) return sme;
        var code = Win32Error(ex);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var subject = isDirectory ? $"'{name}' (or a file inside it)" : $"'{name}'";
        var unchanged = isDirectory ? FolderUnchanged : FileUnchanged;
        var technical = code is null ? ex.Message : $"Win32 {code}: {ex.Message}";
        return code switch
        {
            ErrorSharingViolation or ErrorLockViolation or ErrorUnableToRemoveReplaced =>
                InUse(subject, unchanged, technical, code, ex),
            ErrorAccessDenied =>
                new SafeMutationException(MutationFailureKind.AccessDenied,
                    $"Windows denied write access to {subject}. Check its permissions, that no program has it open, and that " +
                    $"security software (for example Controlled Folder Access) allows IKEMEN Lab to change it. {unchanged}",
                    technical, code, ex),
            ErrorDiskFull or ErrorHandleDiskFull =>
                new SafeMutationException(MutationFailureKind.DiskFull,
                    $"There is not enough disk space to update '{name}'. {unchanged}",
                    technical, code, ex),
            _ => new SafeMutationException(MutationFailureKind.Other,
                    $"Could not update '{name}': {ex.Message}", technical, code, ex)
        };
    }

    public static void EnsureSameVolume(string a, string b)
    {
        var ra = Path.GetPathRoot(Path.GetFullPath(a));
        var rb = Path.GetPathRoot(Path.GetFullPath(b));
        if (!string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase))
        {
            throw new SafeMutationException(MutationFailureKind.UnsafeTarget,
                "Internal error: replacement staging must be on the same drive as the target.",
                $"staging={a} target={b}");
        }
    }

    /// <summary>Copies and flushes to disk (write-through) so the staged bytes are durable before the swap.</summary>
    public static void CopyDurable(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Renames a directory inside one parent (same volume, so it is atomic). Retries briefly while
    /// Explorer, an indexer or a virus scan holds a handle inside it; otherwise throws a classified
    /// error named after <paramref name="displayPath"/>.
    /// </summary>
    private static SafeMutationException InUse(string subject, string unchanged, string technical, int? code, Exception ex)
        => new(MutationFailureKind.InUse,
            $"{subject} is in use by another program (for example IKEMEN GO, VSelect or a text editor). " +
            $"Close it and try again. {unchanged}",
            technical, code, ex);

    public static void MoveDirectory(string source, string destination, string displayPath)
    {
        EnsureSameVolume(source, destination);
        var attempt = 0;
        while (true)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when ((IsTransient(ex) || Win32Error(ex) == ErrorAccessDenied) && attempt < RetryDelays.Count)
            {
                Thread.Sleep(RetryDelays[attempt++]);
            }
            catch (Exception ex) when (Win32Error(ex) == ErrorAccessDenied)
            {
                // Callers stage a folder in the same parent first, which proves write permission there;
                // Windows refuses to rename a folder while a program holds a file inside it open.
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(displayPath));
                throw InUse($"'{name}' (or a file inside it)", FolderUnchanged, $"Win32 5: {ex.Message}", ErrorAccessDenied, ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Classify(ex, displayPath, isDirectory: true);
            }
        }
    }

    /// <summary>
    /// Best-effort recursive delete. Clears ReadOnly first (copied character files often carry it) and
    /// never descends into junctions or symbolic links; Directory.Delete removes such links, not their targets.
    /// </summary>
    public static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return true;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Removes this app's own abandoned staging copies (".ikemenlab-*.tmp" files and folders) older
    /// than the age limit. Staging only ever holds copies, so removing it cannot lose content.
    /// </summary>
    public static void SweepStaleStaging(string directory, TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var file in Directory.EnumerateFiles(directory, ".ikemenlab-*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            foreach (var folder in Directory.EnumerateDirectories(directory, ".ikemenlab-*.tmp"))
            {
                try
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                    if (Directory.GetLastWriteTimeUtc(folder) >= cutoff) continue;
                    TryDeleteDirectory(folder);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
