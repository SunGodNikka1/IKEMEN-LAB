using IKEMENLab.Core.Library;
using IKEMENLab.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace IKEMENLab.Tests;

/// <summary>
/// Opt-in, read-only Date Added diagnostics for a real install (IKEMENLAB_REAL_ROOT). Uses the
/// estimate-only tracker, so nothing is persisted and nothing under the root is touched.
/// </summary>
public class RealDateAddedDiagnostics(ITestOutputHelper output)
{
    [Fact]
    public void CompareCreationTimeWithArrivalEstimate()
    {
        var root = Environment.GetEnvironmentVariable("IKEMENLAB_REAL_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var snapshot = new LibraryIndexService().Index(root);
        output.WriteLine($"characters={snapshot.CharacterCount}");

        string Folder(string id) => Path.Combine(root, "chars", ContentIdentity.TopFolder(id));

        output.WriteLine("OLD (top-level folder CreationTime):");
        foreach (var c in snapshot.Characters.OrderByDescending(c => Directory.GetCreationTimeUtc(Folder(c.Id))).Take(8))
        {
            output.WriteLine($"  {Directory.GetCreationTimeUtc(Folder(c.Id)):u} {c.DisplayName} ({c.Id})");
        }

        output.WriteLine("NEW (arrival estimate: creation, or NTFS change time when moved):");
        foreach (var c in DateAddedSort.Apply(snapshot.Characters, BrowserSortMode.LatestAdded, c => c.DateAddedUtc, c => c.DisplayName).Take(8))
        {
            var t = FileSystemTimes.Read(Folder(c.Id));
            output.WriteLine($"  {c.DateAddedUtc:u} {c.DisplayName} ({c.Id}) created={t?.Created:u} changed={t?.Changed:u} written={t?.LastWrite:u}");
        }

        var muzan = snapshot.Characters.FirstOrDefault(c => c.Id.StartsWith("Muzan", StringComparison.OrdinalIgnoreCase));
        if (muzan is not null)
        {
            var rank = DateAddedSort.Apply(snapshot.Characters, BrowserSortMode.LatestAdded, c => c.DateAddedUtc, c => c.DisplayName)
                .Select((c, i) => (c, i)).First(x => x.c.Id == muzan.Id).i + 1;
            output.WriteLine($"Muzan ({muzan.DefPath}, nested={muzan.Nested}) new rank={rank} estimate={muzan.DateAddedUtc:u}");
        }
    }
}
