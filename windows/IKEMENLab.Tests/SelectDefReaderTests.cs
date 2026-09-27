using IKEMENLab.Core.Models;
using IKEMENLab.Core.SelectDef;
using Xunit;

namespace IKEMENLab.Tests;

public class SelectDefReaderTests
{
    [Theory]
    [InlineData("kfm", "kfm/kfm.def")]
    [InlineData("Pack/kfm", "Pack/kfm.def")]
    [InlineData("Pack/kfm.def", "Pack/kfm.def")]
    [InlineData(@"Pack\Sub\kfm", "Pack/Sub/kfm.def")]
    [InlineData("Noaya_Zenin/Noaya_Zenin/Noaya_Zenin", "Noaya_Zenin/Noaya_Zenin/Noaya_Zenin.def")]
    public void CharacterNamesFollowIkemenRules(string raw, string expected)
    {
        var (kind, candidates) = SelectDefReader.ResolveCharacterName(raw);
        Assert.Equal(SelectDefEntryKind.Content, kind);
        Assert.Equal([expected, "chars/" + expected], candidates);
    }

    [Theory]
    [InlineData("randomselect", SelectDefEntryKind.RandomSelect)]
    [InlineData("RandomSelect", SelectDefEntryKind.RandomSelect)]
    [InlineData("empty", SelectDefEntryKind.Empty)]
    [InlineData("kfm.def", SelectDefEntryKind.Invalid)]
    public void SpecialAndInvalidNames(string raw, SelectDefEntryKind kind)
    {
        Assert.Equal(kind, SelectDefReader.ResolveCharacterName(raw).Kind);
    }

    [Fact]
    public void ParsesSectionsCommentsAndParameters()
    {
        var content = string.Join("\n",
            "; header comment",
            "[Characters]",
            "; Insert your characters below",
            "kfm, stages/kfm.def, music=sound/x.mp3 ; trailing comment",
            ";suave",
            @"Pack\ken",
            "randomselect",
            "",
            "[ExtraStages]",
            "stages/kfm.def",
            @";stages\old.def, order=2",
            "",
            "[Options]",
            "arcade.maxmatches = 6,1");

        var entries = SelectDefReader.Parse(content);
        var chars = entries.Where(e => e.Section == SelectDefSection.Characters).ToList();
        var stages = entries.Where(e => e.Section == SelectDefSection.ExtraStages).ToList();

        Assert.Contains(chars, e => e.RawName == "kfm" && !e.IsCommented && e.CandidateDefPaths[1] == "chars/kfm/kfm.def");
        Assert.Contains(chars, e => e.RawName == "suave" && e.IsCommented);
        Assert.Contains(chars, e => e.RawName == @"Pack\ken" && e.CandidateDefPaths[0] == "Pack/ken.def");
        Assert.Contains(chars, e => e.Kind == SelectDefEntryKind.RandomSelect);
        Assert.Equal(2, stages.Count);
        Assert.Contains(stages, e => e.CandidateDefPaths[0] == "stages/old.def" && e.IsCommented);
        Assert.DoesNotContain(entries, e => e.RawName.StartsWith("arcade", StringComparison.Ordinal));
        Assert.Equal(4, chars.Single(e => e.RawName == "kfm").LineNumber);
    }

    [Fact]
    public void IndexDerivesStatusFromActiveSelectDef()
    {
        using var root = new TestRoot();
        root.Write("chars/kfm/kfm.def", TestRoot.CharDef("Kung Fu Man"));
        root.Write("chars/suave/suave.def", TestRoot.CharDef("Suave"));
        root.Write("chars/Pack/Ken/Ken.def", TestRoot.CharDef("Ken"));
        root.Write("chars/loner/loner.def", TestRoot.CharDef("Loner"));
        root.Write("stages/kfm.def", "[Info]\nname = KFM\n[BGdef]\nspr = kfm.sff\n");
        root.Write("stages/old.def", "[Info]\nname = Old\n[BGdef]\nspr = old.sff\n");
        root.Write("data/select.def", string.Join("\n",
            "[Characters]",
            "kfm",
            ";suave",
            @"Pack\Ken\Ken",
            "ghost",
            "[ExtraStages]",
            "stages/kfm.def",
            @";stages\old.def",
            "stages/gone.def"));

        var index = SelectDefIndex.Build(root.Path, SelectDefLocator.Locate(root.Path, null));

        Assert.True(index.IsAvailable);
        Assert.Equal(ContentStatus.Active, index.CharacterStatus("chars/kfm/kfm.def"));
        Assert.Equal(ContentStatus.Disabled, index.CharacterStatus("chars/suave/suave.def"));
        Assert.Equal(ContentStatus.Active, index.CharacterStatus("chars/Pack/Ken/Ken.def"));
        Assert.Equal(ContentStatus.Unregistered, index.CharacterStatus("chars/loner/loner.def"));
        Assert.Equal(ContentStatus.Active, index.StageStatus("stages/KFM.def"));
        Assert.Equal(ContentStatus.Disabled, index.StageStatus("stages/old.def"));
        Assert.Equal(["ghost", "stages/gone.def"], index.MissingEntries.Select(e => e.RawName));
    }

    [Fact]
    public void ZipArchivedEntriesAreNotReportedMissing()
    {
        using var root = new TestRoot();
        root.WriteBytes("stages/Boat.zip", [0x50, 0x4B, 0x05, 0x06]);
        root.Write("data/select.def", "[ExtraStages]\nstages/Boat.zip/Boat/PASS.def\nstages/Gone.zip/x.def\n");

        var index = SelectDefIndex.Build(root.Path, SelectDefLocator.Locate(root.Path, null));

        Assert.Contains(index.Document!.Stages, e => e.RawName.StartsWith("stages/Boat.zip") && e.IsInArchive);
        Assert.Equal(["stages/Gone.zip/x.def"], index.MissingEntries.Select(e => e.RawName));
    }

    [Fact]
    public void LocatorFollowsMotifFilesSelect()
    {
        using var root = new TestRoot();
        root.Write("data/select.def", "[Characters]\n");
        root.Write("data/mymotif/system.def", "[Files]\nselect = roster.def ; custom\n");
        root.Write("data/mymotif/roster.def", "[Characters]\nkfm\n");

        var custom = SelectDefLocator.Locate(root.Path, "data/mymotif/system.def");
        Assert.Equal(SelectDefLocationSource.Motif, custom.Source);
        Assert.Equal(root.Full("data/mymotif/roster.def"), custom.Path);

        // A motif without its own select.def falls through to data/ (IKEMEN search order).
        root.Write("data/other/system.def", "[Files]\nselect = select.def\n");
        var other = SelectDefLocator.Locate(root.Path, @"data\other\system.def");
        Assert.Equal(root.Full("data/select.def"), other.Path);

        var missingMotif = SelectDefLocator.Locate(root.Path, "data/nope/system.def");
        Assert.Equal(SelectDefLocationSource.DataFallback, missingMotif.Source);
    }
}
