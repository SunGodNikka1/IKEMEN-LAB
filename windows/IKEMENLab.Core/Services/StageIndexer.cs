using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Services;

/// <summary>
/// Read-only stage discovery: top-level .def plus exactly one subdirectory deep.
/// </summary>
public sealed class StageIndexer
{
    public IReadOnlyList<StageEntry> Index(string rootPath, out IReadOnlyList<IndexWarning> warnings)
    {
        var warningList = new List<IndexWarning>();
        var stages = new List<StageEntry>();
        var stagesRoot = Path.Combine(rootPath, "stages");

        if (!Directory.Exists(stagesRoot))
        {
            warnings = warningList;
            return stages;
        }

        foreach (var item in Directory.EnumerateFileSystemEntries(stagesRoot))
        {
            try
            {
                if (File.Exists(item) &&
                    string.Equals(Path.GetExtension(item), ".def", StringComparison.OrdinalIgnoreCase))
                {
                    TryAdd(stagesRoot, item, stages, warningList);
                    continue;
                }

                if (!Directory.Exists(item) || IkemenLabStaging.IsStagingName(item)) continue;

                foreach (var def in Directory.EnumerateFiles(item, "*.def"))
                {
                    TryAdd(stagesRoot, def, stages, warningList);
                }
            }
            catch (Exception ex)
            {
                warningList.Add(new IndexWarning
                {
                    Path = Path.GetRelativePath(stagesRoot, item).Replace('\\', '/'),
                    Message = $"Skipped due to error: {ex.Message}"
                });
            }
        }

        stages.Sort(static (a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        warnings = warningList;
        return stages;
    }

    private static void TryAdd(
        string stagesRoot,
        string defPath,
        List<StageEntry> stages,
        List<IndexWarning> warnings)
    {
        if (!DefContentClassifier.IsValidStageDefFile(defPath))
        {
            return;
        }

        DefParseResult? parsed;
        try
        {
            parsed = DefParser.ParseFile(defPath);
        }
        catch (Exception ex)
        {
            warnings.Add(new IndexWarning
            {
                Path = Path.GetRelativePath(stagesRoot, defPath).Replace('\\', '/'),
                Message = $"Malformed stage DEF skipped: {ex.Message}"
            });
            return;
        }

        var relative = Path.GetRelativePath(stagesRoot, defPath).Replace('\\', '/');
        var bgm = BgmReference(parsed);
        stages.Add(new StageEntry
        {
            Id = relative,
            Name = StageNameExtractor.Extract(defPath),
            Author = string.IsNullOrWhiteSpace(parsed?.Author) ? "Unknown" : parsed!.Author!,
            DefPath = relative,
            ModifiedAtUtc = SafeTime(() => File.GetLastWriteTimeUtc(defPath)),
            BgmReference = bgm,
            BgmFound = bgm is not null && ResolveBgm(Path.GetDirectoryName(stagesRoot)!, defPath, bgm),
            BoundLeft = OptionalInt(parsed?.Value("boundleft", "camera")),
            BoundRight = OptionalInt(parsed?.Value("boundright", "camera"))
        });
    }

    /// <summary>First non-empty [Music] bgmusic/bgm entry (IKEMEN also accepts prefixed ".bgmusic" keys).</summary>
    internal static string? BgmReference(DefParseResult? parsed)
    {
        if (parsed is null || !parsed.SectionValues.TryGetValue("music", out var music)) return null;
        foreach (var key in new[] { "bgmusic", "bgm" })
        {
            if (music.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        }

        return music
            .Where(kv => (kv.Key.EndsWith(".bgmusic", StringComparison.Ordinal) || kv.Key.EndsWith(".bgm", StringComparison.Ordinal))
                         && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Value.Trim())
            .FirstOrDefault();
    }

    private static bool ResolveBgm(string root, string defPath, string reference)
    {
        var normalized = reference.Replace('\\', '/');
        if (Path.IsPathRooted(normalized)) return File.Exists(normalized);
        foreach (var dir in new[] { Path.GetDirectoryName(defPath)!, root, Path.Combine(root, "data"), Path.Combine(root, "sound") })
        {
            if (File.Exists(Path.Combine(dir, normalized))) return true;
        }

        return false;
    }

    private static int? OptionalInt(string? value)
        => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (int)Math.Round(d)
            : null;

    private static DateTime? SafeTime(Func<DateTime> read)
    {
        try { return read(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
