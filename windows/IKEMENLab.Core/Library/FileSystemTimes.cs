using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IKEMENLab.Core.Library;

public readonly record struct FileTimesUtc(DateTime Created, DateTime LastWrite, DateTime? Changed);

/// <summary>
/// Read-only file-system evidence for when content arrived. Used only once per identity, when IKEMEN
/// Lab first establishes a Date Added record; afterwards the stored value is authoritative.
///
/// CreationTime alone is not arrival: moving a folder within the same drive (e.g. from an extracted
/// pack in Downloads into chars/) keeps its old CreationTime. NTFS ChangeTime (MFT record change,
/// updated by a rename/move) exposes such moves: a ChangeTime clearly later than LastWriteTime means
/// the entry was moved or renamed without its contents changing.
/// </summary>
public static class FileSystemTimes
{
    private static readonly TimeSpan MoveTolerance = TimeSpan.FromSeconds(2);

    public static FileTimesUtc? Read(string path)
    {
        try
        {
            var isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return null;
            var created = isDir ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);
            var written = isDir ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
            return new FileTimesUtc(created, written, OperatingSystem.IsWindows() ? ChangeTimeUtc(path) : null);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Best estimate of when <paramref name="path"/> arrived: the later of its creation time and, when
    /// the NTFS record shows a move/rename, its change time. Null if the path does not exist.
    /// </summary>
    public static DateTime? ArrivalEstimateUtc(string path)
    {
        if (Read(path) is not { } t) return null;
        var estimate = t.Created;
        if (t.Changed is { } changed && changed - t.LastWrite > MoveTolerance && changed > estimate)
        {
            estimate = changed;
        }

        return estimate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const int FileBasicInfoClass = 0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileBasicInfo info, uint size);

    private static DateTime? ChangeTimeUtc(string path)
    {
        try
        {
            // FILE_READ_ATTRIBUTES only, full sharing: never blocks or modifies anything.
            using var handle = CreateFileW(Path.GetFullPath(path), FileReadAttributes, ShareAll, IntPtr.Zero,
                OpenExisting, FlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()))
                return null;
            return info.ChangeTime > 0 ? DateTime.FromFileTimeUtc(info.ChangeTime) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }
}
