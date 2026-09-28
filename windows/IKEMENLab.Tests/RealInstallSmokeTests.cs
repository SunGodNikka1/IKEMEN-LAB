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

    /// <summary>
    /// Decodes every portrait and stage preview of the real install into a temporary cache and, when
    /// IKEMENLAB_CONTACT_SHEET is set, writes a contact sheet PNG there for visual inspection.
    /// </summary>
    [Fact]
    public void DecodesRealArtworkReadOnly()
    {
        var root = Environment.GetEnvironmentVariable("IKEMENLAB_REAL_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var snapshot = new LibraryIndexService().Index(root);
        var cacheDir = Path.Combine(Path.GetTempPath(), "ikemenlab-realcache-" + Guid.NewGuid().ToString("N"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var service = new IKEMENLab.Core.Sprites.ArtworkService(root, new IKEMENLab.Core.Sprites.ThumbnailCache(cacheDir));
            var portraits = new List<(string Name, byte[]? Png)>();
            foreach (var c in snapshot.Characters)
            {
                portraits.Add((c.DisplayName, service.CharacterPortraitPng(c)));
            }

            var portraitMs = sw.ElapsedMilliseconds;
            var stages = new List<(string Name, byte[]? Png)>();
            foreach (var s in snapshot.Stages)
            {
                stages.Add((s.Name, service.StagePreviewPng(s)));
            }

            output.WriteLine($"portraits {portraits.Count(p => p.Png is not null)}/{portraits.Count} in {portraitMs} ms");
            output.WriteLine($"stages {stages.Count(p => p.Png is not null)}/{stages.Count} in {sw.ElapsedMilliseconds - portraitMs} ms");
            foreach (var miss in portraits.Where(p => p.Png is null).Take(20)) output.WriteLine("  no portrait: " + miss.Name);
            foreach (var miss in stages.Where(p => p.Png is null).Take(20)) output.WriteLine("  no preview: " + miss.Name);

            sw.Restart();
            foreach (var c in snapshot.Characters) service.CharacterPortraitPng(c);
            output.WriteLine($"warm cache pass {sw.ElapsedMilliseconds} ms");

            var sheet = Environment.GetEnvironmentVariable("IKEMENLAB_CONTACT_SHEET");
            if (!string.IsNullOrWhiteSpace(sheet))
            {
                Directory.CreateDirectory(sheet);
                WriteSheet(Path.Combine(sheet, "portraits.png"), portraits.Select(p => p.Png), 96, 96, 16);
                WriteSheet(Path.Combine(sheet, "stages.png"), stages.Select(p => p.Png), 192, 108, 8);
            }
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    private static void WriteSheet(string path, IEnumerable<byte[]?> pngs, int cellW, int cellH, int columns)
    {
        var images = pngs.ToList();
        var rows = (images.Count + columns - 1) / columns;
        var w = columns * cellW;
        var h = Math.Max(1, rows) * cellH;
        var bgra = new byte[w * h * 4];
        for (var i = 3; i < bgra.Length; i += 4) { bgra[i - 3] = 30; bgra[i - 2] = 24; bgra[i - 1] = 24; bgra[i] = 255; }

        for (var n = 0; n < images.Count; n++)
        {
            if (images[n] is not { } png) continue;
            var decoded = IKEMENLab.Core.Sprites.Png.Decode(png);
            if (decoded is null) continue;
            var fit = IKEMENLab.Core.Sprites.SpriteImageOps.FitWithin(decoded.ToSpriteImage(), cellW - 4, cellH - 4);
            var ox = (n % columns) * cellW + (cellW - fit.Width) / 2;
            var oy = (n / columns) * cellH + (cellH - fit.Height) / 2;
            for (var y = 0; y < fit.Height; y++)
            {
                for (var x = 0; x < fit.Width; x++)
                {
                    var s = (y * fit.Width + x) * 4;
                    var d = ((oy + y) * w + ox + x) * 4;
                    var a = fit.Bgra[s + 3] / 255.0;
                    for (var k = 0; k < 3; k++)
                    {
                        bgra[d + k] = (byte)(fit.Bgra[s + k] * a + bgra[d + k] * (1 - a));
                    }
                }
            }
        }

        File.WriteAllBytes(path, IKEMENLab.Core.Sprites.Png.Encode(new IKEMENLab.Core.Sprites.SpriteImage(w, h, bgra)));
    }
}
