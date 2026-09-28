using IKEMENLab.Core.Config;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace IKEMENLab.Tests;

/// <summary>
/// Opt-in, read-only comparison of the old file-order primary DEF with the shared resolver on a real
/// install (IKEMENLAB_REAL_ROOT). No saved choices are read or written and nothing under the root is touched.
/// </summary>
public class RealPrimaryDefDiagnostics(ITestOutputHelper output)
{
    [Fact]
    public void CompareOldAndNewPrimaryDefs()
    {
        var root = Environment.GetEnvironmentVariable("IKEMENLAB_REAL_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var snapshot = new LibraryIndexService().Index(root);
        var select = SelectDefIndex.Build(root, SelectDefLocator.Locate(root, IkemenConfigReader.Read(root).Motif));
        int multi = 0, changedDef = 0, changedStatus = 0, ambiguous = 0;
        foreach (var c in snapshot.Characters)
        {
            var top = Path.Combine(root, "chars", c.Id.Split('/')[0]);
            var oldDef = OldPrimary(root, top);
            var oldStatus = oldDef is null ? c.Status : select.CharacterStatus(oldDef);
            if (c.DefCandidates.Count > 1) multi++;
            if (c.NeedsDefChoice) ambiguous++;
            var defDiffers = !string.Equals(oldDef, c.DefPath, StringComparison.OrdinalIgnoreCase);
            if (defDiffers) changedDef++;
            if (oldStatus != c.Status) changedStatus++;
            if (c.DefCandidates.Count > 1 || defDiffers || oldStatus != c.Status)
            {
                output.WriteLine($"{c.Id}: old={oldDef} ({oldStatus}) new={c.DefPath} ({c.Status}, {c.PrimaryRule}" +
                                 (c.NeedsDefChoice ? ", NEEDS CHOICE" : "") + $") candidates=[{string.Join(", ", c.DefCandidates.Select(Path.GetFileName))}]" +
                                 (c.ActiveDefPaths.Count > 0 ? $" active=[{string.Join(", ", c.ActiveDefPaths)}]" : ""));
            }
        }

        output.WriteLine($"characters={snapshot.Characters.Count} multiDef={multi} primaryChanged={changedDef} statusChanged={changedStatus} needsChoice={ambiguous}");
    }

    /// <summary>The pre-resolver rule: Folder/Folder.def, else the alphabetically first valid DEF, else one level down.</summary>
    private static string? OldPrimary(string root, string top)
    {
        static string? Pick(string folder)
        {
            var preferred = Path.Combine(folder, Path.GetFileName(folder) + ".def");
            if (File.Exists(preferred) && DefContentClassifier.IsValidCharacterDefFile(preferred)) return preferred;
            return Directory.EnumerateFiles(folder, "*.def").Where(DefContentClassifier.IsValidCharacterDefFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        var def = Pick(top) ?? Directory.EnumerateDirectories(top).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(Pick).FirstOrDefault(d => d is not null);
        return def is null ? null : Path.GetRelativePath(root, def).Replace('\\', '/');
    }
}
