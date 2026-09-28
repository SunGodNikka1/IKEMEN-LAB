using System.Text;
using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Config;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Roster toggles and the other select.def/config writers exercised end-to-end through the shared
/// SafeMutation foundation, on the same volume and (when configured) across volumes like the real
/// install that failed with "Unable to remove the file to be replaced."
/// </summary>
public class RosterRegressionTests
{
    private static RosterActivationService Roster(MutationFixture f) => new(f.Mutations, f.StagingDir);

    private static ContentStatus StatusOf(MutationFixture f, string defPath) =>
        new LibraryIndexService().Index(f.Root).Characters
            .Single(c => c.DefPath.Equals(defPath, StringComparison.OrdinalIgnoreCase)).Status;

    public static IEnumerable<object[]> Layouts()
    {
        yield return ["same"];
        yield return ["cross"];
    }

    private static MutationFixture? Make(string layout)
        => layout == "cross" ? MutationFixture.CrossVolume() : MutationFixture.SameVolume();

    [Theory]
    [MemberData(nameof(Layouts))]
    public void NewlyInstalledCharacterEnableDisableReEnable(string layout)
    {
        using var f = Make(layout);
        if (f is null) return;
        f.WriteChar("kfm");
        f.WriteChar("berdly");
        f.Write("data/select.def", "[Characters]\r\nkfm, stages/kfm.def, order=1\r\n\r\n[ExtraStages]\r\n");
        var roster = Roster(f);

        var enable = roster.SetCharacterEnabled(f.Root, "chars/berdly/berdly.def", enabled: true);
        Assert.True(enable.Success, enable.Error);
        Assert.Equal(ContentStatus.Active, StatusOf(f, "chars/berdly/berdly.def"));
        Assert.Equal("[Characters]\r\nkfm, stages/kfm.def, order=1\r\nberdly\r\n\r\n[ExtraStages]\r\n",
            File.ReadAllText(f.Full("data/select.def")));

        var disable = roster.SetCharacterEnabled(f.Root, "chars/berdly/berdly.def", enabled: false);
        Assert.True(disable.Success, disable.Error);
        Assert.Equal(ContentStatus.Disabled, StatusOf(f, "chars/berdly/berdly.def"));

        var again = roster.SetCharacterEnabled(f.Root, "chars/berdly/berdly.def", enabled: true);
        Assert.True(again.Success, again.Error);
        Assert.Equal(ContentStatus.Active, StatusOf(f, "chars/berdly/berdly.def"));
        Assert.Equal("[Characters]\r\nkfm, stages/kfm.def, order=1\r\nberdly\r\n\r\n[ExtraStages]\r\n",
            File.ReadAllText(f.Full("data/select.def")));
    }

    [Fact]
    public void StaleCommentedDuplicateNoLongerTrapsReEnable()
    {
        using var f = MutationFixture.SameVolume();
        f.WriteChar("kfm");
        f.Write("data/select.def", "[Characters]\n;kfm, old slot\nkfm\n");
        var roster = Roster(f);

        Assert.True(roster.SetCharacterEnabled(f.Root, "chars/kfm/kfm.def", false).Success);
        var reEnable = roster.SetCharacterEnabled(f.Root, "chars/kfm/kfm.def", true);
        Assert.True(reEnable.Success, reEnable.Error);
        Assert.Equal(ContentStatus.Active, StatusOf(f, "chars/kfm/kfm.def"));
    }

    [Fact]
    public void DoubleCommentedEntryReEnables()
    {
        using var f = MutationFixture.SameVolume();
        f.WriteChar("kfm");
        f.Write("data/select.def", "[Characters]\n;;kfm, music=x.mp3\n");
        var result = Roster(f).SetCharacterEnabled(f.Root, "chars/kfm/kfm.def", true);
        Assert.True(result.Success, result.Error);
        Assert.Equal("[Characters]\nkfm, music=x.mp3\n", File.ReadAllText(f.Full("data/select.def")));
    }

    [Fact]
    public void NestedCharacterAndCustomMotifSelectDef()
    {
        using var f = MutationFixture.SameVolume();
        f.WriteChar("Pack/Ken");
        f.Write("save/config.ini", "[Config]\nMotif = data/mine/system.def\n");
        f.Write("data/mine/system.def", "[Files]\nselect = roster.def\n");
        f.Write("data/mine/roster.def", "[Characters]\n");
        f.Write("data/select.def", "[Characters]\n");

        var result = Roster(f).SetCharacterEnabled(f.Root, "chars/Pack/Ken/Ken.def", true);
        Assert.True(result.Success, result.Error);
        Assert.Equal(f.Full("data/mine/roster.def"), result.SelectDefPath);
        Assert.Contains("Pack/Ken/Ken", File.ReadAllText(f.Full("data/mine/roster.def")));
        Assert.Equal("[Characters]\n", File.ReadAllText(f.Full("data/select.def")));
    }

    [Fact]
    public void OnlyTheToggledLineChangesAndLegacyEncodingIsKept()
    {
        using var f = MutationFixture.SameVolume();
        f.WriteChar("kfm");
        var cp1252 = Encoding.GetEncoding(1252);
        var original = "; Liste de personnages créée à la main\n[Characters]\r\nkfm, order=2 ; préféré\r\n[ExtraStages]\n";
        f.WriteBytes("data/select.def", cp1252.GetBytes(original));

        var result = Roster(f).SetCharacterEnabled(f.Root, "chars/kfm/kfm.def", false);

        Assert.True(result.Success, result.Error);
        var expected = original.Replace("kfm, order=2", ";kfm, order=2");
        Assert.Equal(cp1252.GetBytes(expected), File.ReadAllBytes(f.Full("data/select.def")));
    }

    [Fact]
    public void RapidParallelTogglesDoNotLoseUpdates()
    {
        using var f = MutationFixture.SameVolume();
        var names = Enumerable.Range(1, 8).Select(i => "c" + i).ToList();
        foreach (var n in names) f.WriteChar(n);
        f.Write("data/select.def", "[Characters]\n");
        var roster = Roster(f);

        Parallel.ForEach(names, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
            Assert.True(roster.SetCharacterEnabled(f.Root, $"chars/{n}/{n}.def", true).Success));

        foreach (var n in names) Assert.Equal(ContentStatus.Active, StatusOf(f, $"chars/{n}/{n}.def"));
    }

    [Fact]
    public void LockedSelectDefFailsWithActionableMessageAndNoChange()
    {
        using var f = MutationFixture.SameVolume();
        f.WriteChar("kfm");
        var select = f.Write("data/select.def", "[Characters]\nkfm\n");
        var before = File.ReadAllBytes(select);

        RosterActivationResult result;
        using (new FileStream(select, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = Roster(f).SetCharacterEnabled(f.Root, "chars/kfm/kfm.def", false);
        }

        Assert.False(result.Success);
        Assert.Contains("in use", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(select));
        Assert.Equal(ContentStatus.Active, StatusOf(f, "chars/kfm/kfm.def"));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void StageToggle(string layout)
    {
        using var f = Make(layout);
        if (f is null) return;
        f.Write("stages/kfm.def", "[Info]\nname = KFM\n[BGdef]\nspr = kfm.sff\n");
        f.Write("data/select.def", "[Characters]\n[ExtraStages]\n");
        var roster = Roster(f);

        Assert.True(roster.SetStageEnabled(f.Root, "stages/kfm.def", true).Success);
        var stage = () => new LibraryIndexService().Index(f.Root).Stages.Single().Status;
        Assert.Equal(ContentStatus.Active, stage());
        Assert.True(roster.SetStageEnabled(f.Root, "stages/kfm.def", false).Success);
        Assert.Equal(ContentStatus.Disabled, stage());
        Assert.True(roster.SetStageEnabled(f.Root, "stages/kfm.def", true).Success);
        Assert.Equal(ContentStatus.Active, stage());
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void CollectionActivationUsesTheSameFoundation(string layout)
    {
        using var f = Make(layout);
        if (f is null) return;
        f.WriteChar("kfm");
        f.WriteChar("suave");
        f.Write("data/select.def", "[Characters]\nkfm\nsuave\n");
        var store = new CollectionStore(Path.Combine(f.AppDir, "collections.json"));
        var activation = new CollectionActivationService(f.Mutations,
            new ActiveCollectionStore(Path.Combine(f.AppDir, "active.json")), f.StagingDir);
        var index = new LibraryIndexService().Index(f.Root).Characters;

        var only = store.Create(f.Root, "Only KFM");
        only = store.Add(f.Root, only.Id, index.Where(c => c.Id == "kfm"));
        var result = activation.Activate(f.Root, only, index);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ContentStatus.Active, StatusOf(f, "chars/kfm/kfm.def"));
        Assert.Equal(ContentStatus.Disabled, StatusOf(f, "chars/suave/suave.def"));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ConfigWritesUseTheSameFoundation(string layout)
    {
        using var f = Make(layout);
        if (f is null) return;
        f.Write("save/config.ini", "[Config]\nMotif = data/system.def\n[Video]\nVSync = 0\nFullscreen = 0\n[Sound]\nMasterVolume = 80\n");
        var config = new IkemenConfigMutationService(f.Mutations, f.StagingDir);

        Assert.True(config.SetBool(f.Root, ConfigValueKind.VSync, true).Success);
        Assert.True(config.SetBool(f.Root, ConfigValueKind.Fullscreen, true).Success);
        Assert.True(config.SetMasterVolume(f.Root, 55).Success);

        var read = IkemenConfigReader.Read(f.Root);
        Assert.True(read.VSync);
        Assert.True(read.Fullscreen);
        Assert.Equal(55, read.MasterVolume);
    }
}
