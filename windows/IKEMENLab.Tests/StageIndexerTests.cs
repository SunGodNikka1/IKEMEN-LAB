using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public class StageIndexerTests
{
    [Fact]
    public void FindsTopLevelAndOneDeep()
    {
        var root = Path.Combine(Path.GetTempPath(), "ikemenlab-stg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chars"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var stages = Path.Combine(root, "stages");
        Directory.CreateDirectory(stages);
        File.WriteAllText(Path.Combine(stages, "plain.def"), "[StageInfo]\nname = Plain\n\n[BGdef]\nspr = a.sff\n");
        var nested = Path.Combine(stages, "Pack");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "fancy.def"), "[StageInfo]\nname = Fancy\n\n[BGdef]\nspr = b.sff\n");
        // too deep — ignored
        var deep = Path.Combine(nested, "deeper");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "ignored.def"), "[StageInfo]\nname = Ignored\n\n[BGdef]\nspr = c.sff\n");
        try
        {
            var list = new StageIndexer().Index(root, out _);
            Assert.Equal(2, list.Count);
            Assert.Contains(list, s => s.Id == "plain.def");
            Assert.Contains(list, s => s.Id == "Pack/fancy.def");
        }
        finally { Directory.Delete(root, true); }
    }
}
