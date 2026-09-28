using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Tests;

/// <summary>
/// Disposable IKEMEN-like root plus app-owned mutation folders. By default everything lives under
/// %TEMP%. When <c>IKEMENLAB_CROSS_VOLUME_DIR</c> points at a directory on a different drive, the
/// IKEMEN root is created there while backups/manifests/staging stay under %TEMP% — the layout of a
/// real install on D: with %LOCALAPPDATA% on C: that exposed the ReplaceFile defect.
/// </summary>
internal sealed class MutationFixture : IDisposable
{
    public const string CrossVolumeVariable = "IKEMENLAB_CROSS_VOLUME_DIR";

    private MutationFixture(string root, string appDir)
    {
        DefFileReader.EnsureEncodingsRegistered();
        Root = root;
        AppDir = appDir;
        foreach (var d in new[] { "chars", "stages", "data", "save" }) Directory.CreateDirectory(Path.Combine(Root, d));
        Directory.CreateDirectory(OpsDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(StagingDir);
        Mutations = new SafeMutationService(OpsDir, BackupDir);
    }

    public string Root { get; }
    public string AppDir { get; }
    public string OpsDir => Path.Combine(AppDir, "ops");
    public string BackupDir => Path.Combine(AppDir, "backups");
    public string StagingDir => Path.Combine(AppDir, "staging");
    public SafeMutationService Mutations { get; }

    public bool IsCrossVolume =>
        !string.Equals(Path.GetPathRoot(Root), Path.GetPathRoot(AppDir), StringComparison.OrdinalIgnoreCase);

    public static MutationFixture SameVolume()
    {
        var id = Guid.NewGuid().ToString("N");
        return new MutationFixture(
            Path.Combine(Path.GetTempPath(), "ikemenlab-mut-root-" + id),
            Path.Combine(Path.GetTempPath(), "ikemenlab-mut-app-" + id));
    }

    /// <summary>Null when no second volume is configured for this run.</summary>
    public static MutationFixture? CrossVolume()
    {
        var dir = Environment.GetEnvironmentVariable(CrossVolumeVariable);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        var id = Guid.NewGuid().ToString("N");
        var fixture = new MutationFixture(
            Path.Combine(dir, "ikemenlab-mut-root-" + id),
            Path.Combine(Path.GetTempPath(), "ikemenlab-mut-app-" + id));
        if (fixture.IsCrossVolume) return fixture;
        fixture.Dispose();
        return null;
    }

    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Write(string relative, string content)
    {
        var full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        var full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string StageSource(string content)
    {
        var path = Path.Combine(StagingDir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    public void WriteChar(string folder, string? defName = null)
    {
        defName ??= Path.GetFileName(folder);
        Write($"chars/{folder}/{defName}.def",
            $"[Info]\nname = {defName}\nauthor = Test\n\n[Files]\ncmd = x.cmd\ncns = x.cns\nsprite = x.sff\nanim = x.air\n");
    }

    public void Dispose()
    {
        foreach (var dir in new[] { Root, AppDir })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
