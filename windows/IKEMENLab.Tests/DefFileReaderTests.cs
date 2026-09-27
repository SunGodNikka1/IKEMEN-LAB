using System.Text;
using IKEMENLab.Core.Parsing;
using Xunit;

namespace IKEMENLab.Tests;

public class DefFileReaderTests
{
    [Fact]
    public void ReadsUtf8WithBom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ikemenlab-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "bom.def");
            var payload = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("name = BomChar\n")).ToArray();
            File.WriteAllBytes(path, payload);
            var content = DefFileReader.ReadFileContent(path);
            Assert.NotNull(content);
            Assert.StartsWith("name = BomChar", content);
            Assert.DoesNotContain('\uFEFF', content);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReturnsNullForMissingFile()
    {
        Assert.Null(DefFileReader.ReadFileContent(Path.Combine(Path.GetTempPath(), "no-such-file-" + Guid.NewGuid() + ".def")));
    }
}
