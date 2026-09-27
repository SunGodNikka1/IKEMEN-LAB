using IKEMENLab.Core.Models;
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

                if (!Directory.Exists(item)) continue;

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
        stages.Add(new StageEntry
        {
            Id = relative,
            Name = StageNameExtractor.Extract(defPath),
            Author = string.IsNullOrWhiteSpace(parsed?.Author) ? "Unknown" : parsed!.Author!,
            DefPath = relative
        });
    }
}
