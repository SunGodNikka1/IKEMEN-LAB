using IKEMENLab.Core.Models;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using IKEMENLab.Core.Validation;
using Xunit;
using Xunit.Abstractions;

namespace IKEMENLab.Tests;

/// <summary>
/// Opt-in, read-only smoke against a real installation. Set IKEMENLAB_REAL_ROOT to run it;
/// otherwise it is a no-op. It asserts that indexing and validation leave the watched files untouched.
/// </summary>
public class RealInstallSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void IndexesRealInstallReadOnly()
    {
        var root = Environment.GetEnvironmentVariable("IKEMENLAB_REAL_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var watched = new[] { "data/select.def", "save/config.ini" }
            .Select(r => Path.Combine(root, r))
            .Where(File.Exists)
            .ToDictionary(p => p, p => (File.GetLastWriteTimeUtc(p), new FileInfo(p).Length));
        var topLevel = Directory.EnumerateFileSystemEntries(root).Count();

        var snapshot = new LibraryIndexService().Index(root);
        var results = ContentValidator.ValidateLibrary(snapshot);
        var storage = StorageCalculator.Calculate(root);

        output.WriteLine($"characters={snapshot.CharacterCount} nested={snapshot.NestedCharacterCount} stages={snapshot.StageCount}");
        output.WriteLine($"active chars={snapshot.ActiveCharacterCount} disabled chars={snapshot.Characters.Count(c => c.Status == ContentStatus.Disabled)}");
        output.WriteLine($"active stages={snapshot.ActiveStageCount} disabled stages={snapshot.Stages.Count(s => s.Status == ContentStatus.Disabled)}");
        output.WriteLine($"select.def={snapshot.SelectDef?.Location.Path} via {snapshot.SelectDef?.Location.Source}");
        output.WriteLine($"select.def missing={snapshot.SelectDef?.MissingEntries.Count} invalid={snapshot.SelectDef?.InvalidEntries.Count}");
        foreach (var m in snapshot.SelectDef?.MissingEntries.Take(15) ?? [])
        {
            output.WriteLine($"  missing L{m.LineNumber}: {m.RawName} => {string.Join(" | ", m.CandidateDefPaths)}");
        }
        var rosterChars = snapshot.SelectDef?.Document?.Characters
            .Count(e => e.Kind == SelectDefEntryKind.Content && !e.IsCommented) ?? 0;
        output.WriteLine($"roster content lines={rosterChars}");
        output.WriteLine($"config vsync={snapshot.Config.VSync} fullscreen={snapshot.Config.Fullscreen} volume={snapshot.Config.MasterVolume} motif={snapshot.Config.Motif}");
        output.WriteLine($"storage={storage} ({(storage is null ? "-" : StorageCalculator.Format(storage.Value))})");
        output.WriteLine($"validation results={results.Count} errors={results.Sum(r => r.ErrorCount)} warnings={results.Sum(r => r.WarningCount)}");
        foreach (var r in results.Take(12))
        {
            output.WriteLine($"  [{r.ContentType}] {r.ContentName}: " +
                string.Join("; ", r.Issues.Where(i => i.Severity != ValidationSeverity.Info).Select(i => i.Message)));
        }
        foreach (var recent in RecentContent.FromSnapshot(snapshot))
        {
            output.WriteLine($"  recent {recent.Type} {recent.Name} {recent.InstalledAtUtc:u} {recent.Status}");
        }

        foreach (var (path, stamp) in watched)
        {
            Assert.Equal(stamp, (File.GetLastWriteTimeUtc(path), new FileInfo(path).Length));
        }
        Assert.Equal(topLevel, Directory.EnumerateFileSystemEntries(root).Count());
    }
}
