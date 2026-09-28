namespace IKEMENLab.Core.Mutations;

/// <summary>
/// Names of the transient folders SafeMutation creates beside a target (".ikemenlab-{operation}.tmp",
/// ".old", "-restore.tmp"). Library scans skip them so an in-flight or interrupted install never
/// appears as a second copy of a character or stage.
/// </summary>
public static class IkemenLabStaging
{
    public const string Prefix = ".ikemenlab-";

    public static bool IsStagingName(string path)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            .StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
