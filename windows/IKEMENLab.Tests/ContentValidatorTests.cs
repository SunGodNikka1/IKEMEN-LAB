using IKEMENLab.Core.Services;
using IKEMENLab.Core.Validation;
using Xunit;

namespace IKEMENLab.Tests;

public class ContentValidatorTests
{
    [Fact]
    public void CleanCharacterHasNoIssues()
    {
        using var root = new TestRoot();
        WriteCompleteChar(root, "kfm");
        var snapshot = new LibraryIndexService().Index(root.Path);
        var result = ContentValidator.ValidateCharacter(root.Path, snapshot.Characters.Single());
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void SharedFilesResolveThroughDataFolder()
    {
        using var root = new TestRoot();
        WriteCompleteChar(root, "kfm", cns: "common1.cns");
        root.Write("data/common1.cns", "; shared");
        var snapshot = new LibraryIndexService().Index(root.Path);
        Assert.Empty(ContentValidator.ValidateCharacter(root.Path, snapshot.Characters.Single()).Issues);
    }

    [Fact]
    public void ReportsMissingAndMismatchedResources()
    {
        using var root = new TestRoot();
        root.Write("chars/Zed's/Zed's.def",
            "[Info]\nname = Zed\n[Files]\nsprite = zeds.sff\nanim = zed.air\ncmd = zed.cmd\nsound = zed.snd\n");
        root.Write("chars/Zed's/zed's.sff", "x");
        root.Write("chars/Zed's/zed.air", "x");
        root.Write("chars/Zed's/zed.cmd", "x");

        var snapshot = new LibraryIndexService().Index(root.Path);
        var result = ContentValidator.ValidateCharacter(root.Path, snapshot.Characters.Single());

        Assert.Contains(result.Issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("'cns'"));
        Assert.Contains(result.Issues, i => i.Message.Contains("mismatch") && i.Message.Contains("zed's.sff"));
        Assert.Contains(result.Issues, i => i.Severity == ValidationSeverity.Warning && i.Message.Contains("Sound file not found"));
        Assert.Contains(result.Issues, i => i.Severity == ValidationSeverity.Info && i.Message.Contains("apostrophe"));
    }

    [Fact]
    public void StageWithoutSpriteIsAnError()
    {
        using var root = new TestRoot();
        root.Write("stages/bare.def",
            "[Info]\nname = Bare Stage\n[BGdef]\n[BG 1]\nspriteno = 0,0\n[Camera]\nboundleft=-10\n");
        var snapshot = new LibraryIndexService().Index(root.Path);
        var stage = Assert.Single(snapshot.Stages);
        var result = ContentValidator.ValidateStage(root.Path, stage);
        Assert.Contains(result.Issues, i => i.Severity == ValidationSeverity.Error && i.Message.Contains("spr"));
    }

    [Fact]
    public void LibraryValidationIncludesMissingRosterEntries()
    {
        using var root = new TestRoot();
        WriteCompleteChar(root, "kfm");
        root.Write("data/select.def", "[Characters]\nkfm\nghost\n");
        var snapshot = new LibraryIndexService().Index(root.Path);
        var results = ContentValidator.ValidateLibrary(snapshot);
        var roster = Assert.Single(results);
        Assert.Equal("select.def", roster.ContentType);
        Assert.Contains(roster.Issues, i => i.Message.Contains("'ghost'"));
    }

    [Fact]
    public void ValidationIsReadOnly()
    {
        using var root = new TestRoot();
        root.Write("chars/Zed's/Zed's.def", "[Info]\nname = Zed\n[Files]\nsprite = zeds.sff\n");
        root.Write("chars/Zed's/zed's.sff", "x");
        var before = Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.GetLastWriteTimeUtc(f), File.ReadAllText(f)));

        ContentValidator.ValidateLibrary(new LibraryIndexService().Index(root.Path));

        var after = Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.GetLastWriteTimeUtc(f), File.ReadAllText(f)));
        Assert.Equal(before, after);
    }

    private static void WriteCompleteChar(TestRoot root, string id, string cns = "x.cns")
    {
        root.Write($"chars/{id}/{id}.def",
            $"[Info]\nname = {id}\n[Files]\nsprite = x.sff\nanim = x.air\ncmd = x.cmd\ncns = {cns}\n");
        foreach (var f in new[] { "x.sff", "x.air", "x.cmd" }) root.Write($"chars/{id}/{f}", "x");
        if (cns == "x.cns") root.Write($"chars/{id}/x.cns", "x");
    }
}
