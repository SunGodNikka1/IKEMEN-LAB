using System.Diagnostics;
using System.Text;
using IKEMENLab.Cli;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Query;
using Xunit;
using Xunit.Abstractions;

namespace IKEMENLab.Tests;

/// <summary>
/// Opt-in, read-only sweep of a real installation: <c>IKEMENLAB_REAL_ROOT=D:\IKEMEN</c>. Every character's first character DEF is indexed;
/// nothing may throw, every relationship must carry evidence, and no interpretation may claim to be a literal fact. Set
/// <c>IKEMENLAB_XRAY_DUMP_DIR</c> to also write a per-character summary (counts, timing, parse notes, unknown-target counts) as text,
/// and <c>IKEMENLAB_XRAY_CHARACTER=Valentine</c> to restrict the sweep to one folder.
/// </summary>
public class XRayRealInstallTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryRealCharacterIndexesWithoutThrowing_AndHonoursTheEvidenceContract()
    {
        var root = Environment.GetEnvironmentVariable("IKEMENLAB_REAL_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "chars"))) return;   // opt-in

        var only = Environment.GetEnvironmentVariable("IKEMENLAB_XRAY_CHARACTER");
        var dumpDir = Environment.GetEnvironmentVariable("IKEMENLAB_XRAY_DUMP_DIR");
        if (!string.IsNullOrWhiteSpace(dumpDir)) Directory.CreateDirectory(dumpDir);

        var summary = new StringBuilder();
        var count = 0;
        var total = Stopwatch.StartNew();
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, "chars")).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(dir);
            if (only is not null && !folder.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            var def = Directory.EnumerateFiles(dir, "*.def").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => DefContentClassifier.IsValidCharacterDefFile(f));
            if (def is null) continue;

            var entry = new CharacterEntry
            {
                Id = folder, DisplayName = folder, Name = folder, Author = "", VersionDate = "",
                DefPath = "chars/" + folder + "/" + Path.GetFileName(def), FolderPath = "chars/" + folder
            };
            var sw = Stopwatch.StartNew();
            var index = CharacterSemanticIndexer.Build(root, entry);
            sw.Stop();
            count++;

            Assert.All(index.Relationships, r =>
            {
                Assert.NotEmpty(r.Evidence);
                Assert.NotEqual(Confidence.RuntimeVerified, r.Confidence);
                Assert.Equal(EvidenceRules.ConfidenceOf(r.Evidence[0].RuleId), r.Confidence);
            });
            foreach (var o in index.Objects)
                Assert.All(o.Labels, l => Assert.NotEqual(Confidence.StaticProven, l.Confidence));

            // The JSON must round-trip through the serializer for every real character too.
            Assert.False(string.IsNullOrEmpty(XRayJson.Index(index)));

            var unknown = index.Relationships.Count(r => r.Confidence == Confidence.Unknown);
            var inferred = index.Relationships.Count(r => r.Confidence == Confidence.Inferred);
            var line = $"{folder,-32} {sw.ElapsedMilliseconds,6} ms  objects {index.Objects.Count,6}  rels {index.Relationships.Count,6}  " +
                       $"states {index.Of(ObjectKind.State).Count(s => !s.IsStub),4}  abilities {index.Of(ObjectKind.Ability).Count(),3}  " +
                       $"helpers {index.Of(ObjectKind.Helper).Count(),3}  vars {index.Of(ObjectKind.Variable).Count(),3}  " +
                       $"unknown {unknown,4}  inferred {inferred,4}  notes {index.Diagnostics.Count,4}";
            summary.AppendLine(line);
            output.WriteLine(line);

            if (!string.IsNullOrWhiteSpace(dumpDir))
            {
                var notes = new StringBuilder(line).AppendLine();
                foreach (var d in index.Diagnostics)
                    notes.AppendLine($"  {d.Severity} {d.Code}: {d.Message}");
                File.WriteAllText(Path.Combine(dumpDir, folder + ".xray.txt"), notes.ToString());
            }
        }

        output.WriteLine($"{count} characters in {total.Elapsed.TotalSeconds:0.0}s");
        if (!string.IsNullOrWhiteSpace(dumpDir)) File.WriteAllText(Path.Combine(dumpDir, "_summary.txt"), summary.ToString());
    }

    [Fact]
    public void CliCanIndexARealCharacterFolder()
    {
        var root = Environment.GetEnvironmentVariable("IKEMENLAB_REAL_ROOT");
        var only = Environment.GetEnvironmentVariable("IKEMENLAB_XRAY_CHARACTER");
        if (string.IsNullOrWhiteSpace(root) || only is null || !Directory.Exists(Path.Combine(root, "chars", only))) return;   // opt-in

        var o = new StringWriter();
        Assert.Equal(0, CliApp.Run(["xray", "index", Path.Combine(root, "chars", only), "--root", root], o, new StringWriter()));
        Assert.Contains("ikemenlab.xray/1", o.ToString());
    }
}
