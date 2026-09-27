using IKEMENLab.Core.Parsing;
using Xunit;

namespace IKEMENLab.Tests;

public class StageNameExtractorTests
{
    [Fact]
    public void UsesName()
    {
        Assert.Equal("Avalon", StageNameExtractor.ExtractFromContent("name = Avalon\n"));
    }

    [Fact]
    public void CommentedRealNamePattern()
    {
        Assert.Equal("Avalon", StageNameExtractor.ExtractFromContent("name = \"O\";\"Avalon\"\n"));
    }

    [Fact]
    public void ShortNameFallsBackToFilename()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ikemenlab-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "cool_stage-name.def");
            File.WriteAllText(path, "name = \"X\"\n");
            Assert.Equal("cool stage name", StageNameExtractor.Extract(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
