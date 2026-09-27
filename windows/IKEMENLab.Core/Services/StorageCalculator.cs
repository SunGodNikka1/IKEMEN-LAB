namespace IKEMENLab.Core.Services;

/// <summary>
/// Read-only disk usage of chars/ + stages/ (the macOS "Storage Used" definition).
/// Reparse points are skipped so junctions cannot cause double counting or loops.
/// </summary>
public static class StorageCalculator
{
    public static long? Calculate(string root, CancellationToken cancellationToken = default)
    {
        var chars = Path.Combine(root, "chars");
        var stages = Path.Combine(root, "stages");
        if (!Directory.Exists(chars) || !Directory.Exists(stages)) return null;

        return DirectorySize(chars, cancellationToken) + DirectorySize(stages, cancellationToken);
    }

    public static long DirectorySize(string directory, CancellationToken cancellationToken = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        long total = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                total += file.Length;
            }
            catch (IOException)
            {
                // File vanished mid-scan; ignore.
            }
        }

        return total;
    }

    /// <summary>Decimal (1000-based) units with one fraction digit, like macOS ByteCountFormatter(.file).</summary>
    public static string Format(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";

        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return unit == 1
            ? $"{Math.Round(value):0} {units[unit]}"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}
