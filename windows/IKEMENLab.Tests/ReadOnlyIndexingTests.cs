using System.Security.Cryptography;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public class ReadOnlyIndexingTests
{
    [Fact]
    public void IndexingDoesNotModifyOrCreateFilesUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ikemenlab-ro-" + Guid.NewGuid().ToString("N"));
        BuildTree(root);
        var before = Snapshot(root);
        try
        {
            var snapshot = new LibraryIndexService().Index(root);
            Assert.True(snapshot.Installation.CanBrowse);
            Assert.NotEmpty(snapshot.Characters);
            Assert.NotEmpty(snapshot.Stages);

            var after = Snapshot(root);
            Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
            foreach (var key in before.Keys)
            {
                Assert.Equal(before[key].Hash, after[key].Hash);
                Assert.Equal(before[key].Length, after[key].Length);
                Assert.Equal(before[key].LastWriteUtc, after[key].LastWriteUtc);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BuildTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "chars", "Ryu"));
        Directory.CreateDirectory(Path.Combine(root, "chars", "(Pack) Ken", "Ken"));
        Directory.CreateDirectory(Path.Combine(root, "stages", "Pack"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "save"));
        File.WriteAllText(Path.Combine(root, "chars", "Ryu", "Ryu.def"),
            "[Info]\nname = Ryu\nauthor = Capcom\n\n[Files]\ncmd = r.cmd\ncns = r.cns\n");
        File.WriteAllText(Path.Combine(root, "chars", "(Pack) Ken", "Ken", "Ken.def"),
            "[Info]\nname = Ken\nauthor = Capcom\n\n[Files]\ncmd = k.cmd\ncns = k.cns\n");
        File.WriteAllText(Path.Combine(root, "stages", "plain.def"),
            "[StageInfo]\nname = Plain\n\n[BGdef]\nspr = a.sff\n");
        File.WriteAllText(Path.Combine(root, "stages", "Pack", "fancy.def"),
            "[StageInfo]\nname = Fancy\n\n[BGdef]\nspr = b.sff\n");
        File.WriteAllText(Path.Combine(root, "data", "select.def"), "; roster\n");
        File.WriteAllText(Path.Combine(root, "save", "config.ini"), "[Config]\n");
        File.WriteAllBytes(Path.Combine(root, "Ikemen_GO.exe"), [0x4D, 0x5A]); // fake MZ header
    }

    private static Dictionary<string, FileStamp> Snapshot(string root)
    {
        var map = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var info = new FileInfo(file);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            map[rel] = new FileStamp(hash, info.Length, info.LastWriteTimeUtc);
        }
        return map;
    }

    private sealed record FileStamp(string Hash, long Length, DateTime LastWriteUtc);
}
