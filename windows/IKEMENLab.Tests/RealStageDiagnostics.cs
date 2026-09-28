using IKEMENLab.Core.Services;
using IKEMENLab.Core.Sprites;
using Xunit;
using Xunit.Abstractions;

namespace IKEMENLab.Tests;

/// <summary>Opt-in diagnostics (IKEMENLAB_REAL_ROOT + IKEMENLAB_STAGE_INDEXES="1,23,24").</summary>
public class RealStageDiagnostics(ITestOutputHelper output)
{
    [Fact]
    public void DescribeStages()
    {
        var root = Environment.GetEnvironmentVariable("IKEMENLAB_REAL_ROOT");
        var indexes = Environment.GetEnvironmentVariable("IKEMENLAB_STAGE_INDEXES");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(indexes)) return;

        var snapshot = new LibraryIndexService().Index(root);
        var cacheDir = Path.Combine(Path.GetTempPath(), "ikemenlab-diag-" + Guid.NewGuid().ToString("N"));
        var service = new ArtworkService(root, new ThumbnailCache(cacheDir));
        foreach (var i in indexes.Split(',').Select(int.Parse))
        {
            var stage = snapshot.Stages[i];
            var sffPath = service.ResolveStageSprite(stage);
            output.WriteLine($"[{i}] {stage.Name} ({stage.DefPath}) sff={sffPath}");
            if (sffPath is null) continue;
            using var sff = SffFile.Open(sffPath)!;
            var pick = SpriteExtractor.ExtractStagePreview(sff);
            output.WriteLine($"   v{sff.Version}.{sff.VersionLo2} sprites={sff.Sprites.Count} pick={pick?.Group},{pick?.Number} {pick?.Image.Width}x{pick?.Image.Height}");
            var dump = Environment.GetEnvironmentVariable("IKEMENLAB_DUMP_DIR");
            foreach (var s in sff.Sprites.Take(6))
            {
                var (w, h) = sff.Dimensions(s);
                output.WriteLine($"     sprite {s.Index}: {s.Group},{s.Number} {w}x{h}");
                if (!string.IsNullOrWhiteSpace(dump) && sff.Decode(s) is { } img)
                {
                    Directory.CreateDirectory(dump);
                    var small = SpriteImageOps.FitWithin(img, 320, 240);
                    File.WriteAllBytes(Path.Combine(dump, $"stage{i}_sprite{s.Index}.png"), Png.Encode(small));
                }
            }
        }

        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
    }
}
