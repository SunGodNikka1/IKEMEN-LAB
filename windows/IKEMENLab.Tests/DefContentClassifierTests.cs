using IKEMENLab.Core.Parsing;
using Xunit;

namespace IKEMENLab.Tests;

public class DefContentClassifierTests
{
    [Fact]
    public void ValidCharacter()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "ryu.def");
            File.WriteAllText(path, "[Info]\nname = Ryu\n\n[Files]\ncmd = ryu.cmd\ncns = ryu.cns\nsprite = ryu.sff\n");
            Assert.True(DefContentClassifier.IsValidCharacterDefFile(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StoryboardExcluded()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "intro.def");
            File.WriteAllText(path, "[SceneDef]\nname = Intro\n");
            Assert.False(DefContentClassifier.IsValidCharacterDefFile(path));
            Assert.True(DefContentClassifier.IsStoryboardDefFile(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StageNotCharacter()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "stage.def");
            File.WriteAllText(path, "[StageInfo]\nname = Dojo\n\n[BGdef]\nspr = stage.sff\n");
            Assert.False(DefContentClassifier.IsValidCharacterDefFile(path));
            Assert.True(DefContentClassifier.IsValidStageDefFile(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CharacterWithBgdefStillValidIfHasFiles()
    {
        Assert.True(DefContentClassifier.IsValidCharacterContent(
            "[BGdef]\nspr = x.sff\n[Files]\ncmd = a.cmd\n"));
    }

    private static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "ikemenlab-cls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
