using System.Globalization;

namespace IKEMENLab.Core.Characters;

/// <summary>
/// Port of the macOS VersionDateFormatter: normalises DEF "versiondate" strings to MM/dd/yyyy,
/// trying the same input patterns in the same order. Unrecognised strings are returned trimmed.
/// </summary>
public static class VersionDateFormatter
{
    private static readonly string[] InputFormats =
    [
        "dd.MM.yyyy",
        "MM.dd.yyyy",
        "dd/MM/yyyy",
        "MM/dd/yyyy",
        "yyyy-MM-dd",
        "MM/dd/yy",
        "dd.MM.yy",
        "MM,dd,yyyy",
        "dd,MM,yyyy"
    ];

    // Lenient variants (single-digit day/month) tried after the exact list.
    private static readonly string[] LenientFormats =
    [
        "d.M.yyyy", "M.d.yyyy", "d/M/yyyy", "M/d/yyyy", "yyyy-M-d", "M/d/yy", "d.M.yy", "M,d,yyyy", "d,M,yyyy"
    ];

    public static string Format(string? versionDate)
    {
        var trimmed = versionDate?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return string.Empty;

        foreach (var formats in new[] { InputFormats, LenientFormats })
        {
            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(trimmed, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                }
            }
        }

        return trimmed;
    }
}
