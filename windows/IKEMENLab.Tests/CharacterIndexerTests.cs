using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public class CharacterIndexerTests
{
    [Fact]
    public void FindsTopLevelAndNested()
    {
        var root = FixtureRoot();
        try
        {
            // top-level
            WriteChar(Path.Combine(root, "chars", "Ryu"), "Ryu.def", "Ryu", "Capcom");
            // nested
            var nestedParent = Path.Combine(root, "chars", "(Pack) Ken");
            WriteChar(Path.Combine(nestedParent, "Ken"), "Ken.def", "Ken", "Capcom");
            // empty folder warning
            Directory.CreateDirectory(Path.Combine(root, "chars", "Empty"));

            var chars = new CharacterIndexer().Index(root, out var warnings);
            Assert.Equal(2, chars.Count);
            Assert.Contains(chars, c => c.Id == "Ryu" && !c.Nested);
            Assert.Contains(chars, c => c.Id == "(Pack) Ken/Ken" && c.Nested);
            Assert.Contains(warnings, w => w.Path == "Empty");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PrefersMatchingDefName()
    {
        var root = FixtureRoot();
        try
        {
            var folder = Path.Combine(root, "chars", "Hero");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "aaa.def"), CharDef("AAA"));
            File.WriteAllText(Path.Combine(folder, "Hero.def"), CharDef("Hero"));
            var chars = new CharacterIndexer().Index(root, out _);
            Assert.Single(chars);
            Assert.Equal("Hero", chars[0].DisplayName);
            Assert.EndsWith("Hero.def", chars[0].DefPath.Replace('\\', '/'));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string FixtureRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ikemenlab-char-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chars"));
        Directory.CreateDirectory(Path.Combine(root, "stages"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        return root;
    }

    private static void WriteChar(string folder, string defName, string name, string author)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, defName),
            $"[Info]\nname = {name}\nauthor = {author}\n\n[Files]\ncmd = x.cmd\ncns = x.cns\nair = x.air\n");
    }

    private static string CharDef(string name) =>
        $"[Info]\nname = {name}\nauthor = Test\n\n[Files]\ncmd = x.cmd\ncns = x.cns\n";
}
