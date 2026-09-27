using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.SelectDef;

public enum SelectDefLocationSource
{
    /// <summary>Resolved through the configured motif's system.def [Files] select.</summary>
    Motif,

    /// <summary>Motif unavailable; fell back to data/select.def.</summary>
    DataFallback,

    NotFound
}

public sealed record SelectDefLocation(string? Path, SelectDefLocationSource Source, string? MotifPath);

/// <summary>
/// Finds the select.def IKEMEN actually loads: config Motif → system.def [Files] select,
/// searched in the motif folder, then the root, then data/ (IKEMEN's motif file search order).
/// </summary>
public static class SelectDefLocator
{
    public const string DefaultMotif = "data/system.def";

    public static SelectDefLocation Locate(string root, string? motif)
    {
        var motifRelative = string.IsNullOrWhiteSpace(motif) ? DefaultMotif : motif.Trim();
        var motifPath = Path.GetFullPath(Path.Combine(root, SelectDefReader.NormalizeSeparators(motifRelative)));

        if (File.Exists(motifPath))
        {
            var parsed = DefParser.ParseFile(motifPath);
            var selectName = parsed?.Value("select", "files");
            if (string.IsNullOrWhiteSpace(selectName)) selectName = "select.def";
            selectName = SelectDefReader.NormalizeSeparators(selectName.Trim());

            var motifDir = Path.GetDirectoryName(motifPath)!;
            foreach (var dir in new[] { motifDir, root, Path.Combine(root, "data") })
            {
                var candidate = Path.GetFullPath(Path.Combine(dir, selectName));
                if (File.Exists(candidate))
                {
                    return new SelectDefLocation(candidate, SelectDefLocationSource.Motif, motifPath);
                }
            }
        }

        var fallback = Path.Combine(root, "data", "select.def");
        return File.Exists(fallback)
            ? new SelectDefLocation(Path.GetFullPath(fallback), SelectDefLocationSource.DataFallback,
                File.Exists(motifPath) ? motifPath : null)
            : new SelectDefLocation(null, SelectDefLocationSource.NotFound, File.Exists(motifPath) ? motifPath : null);
    }
}
