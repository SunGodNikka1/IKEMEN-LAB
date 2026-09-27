using IKEMENLab.Core.Config;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public class DashboardServicesTests
{
    [Fact]
    public void ReadsConfigIniValues()
    {
        using var root = new TestRoot();
        root.Write("save/config.ini", string.Join("\n",
            "[Config]",
            "; Motif to use.",
            "Motif             = data/ikemen1/system.def",
            "[Video]",
            "Fullscreen               = 1",
            "VSync                    = 0",
            "[Sound]",
            "MasterVolume         = 85"));

        var config = IkemenConfigReader.Read(root.Path);
        Assert.True(config.Exists);
        Assert.True(config.Fullscreen);
        Assert.False(config.VSync);
        Assert.Equal(85, config.MasterVolume);
        Assert.Equal("data/ikemen1/system.def", config.Motif);
    }

    [Fact]
    public void ReadsLegacyConfigJson()
    {
        using var root = new TestRoot();
        root.Write("save/config.json",
            "{ \"Fullscreen\": false, \"VRetrace\": 1, \"MasterVolume\": 40, \"Motif\": \"data/system.def\" }");
        var config = IkemenConfigReader.Read(root.Path);
        Assert.False(config.Fullscreen);
        Assert.True(config.VSync);
        Assert.Equal(40, config.MasterVolume);
    }

    [Fact]
    public void MissingConfigIsNeutral()
    {
        using var root = new TestRoot();
        var config = IkemenConfigReader.Read(root.Path);
        Assert.False(config.Exists);
        Assert.Null(config.VSync);
        Assert.Null(config.MasterVolume);
    }

    [Fact]
    public void StorageSumsCharsAndStagesOnly()
    {
        using var root = new TestRoot();
        root.WriteBytes("chars/a/a.sff", new byte[1500]);
        root.WriteBytes("stages/b.sff", new byte[500]);
        root.WriteBytes("data/huge.bin", new byte[9000]);
        Assert.Equal(2000, StorageCalculator.Calculate(root.Path));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(999, "999 bytes")]
    [InlineData(1500, "2 KB")]
    [InlineData(876_100_000, "876.1 MB")]
    [InlineData(2_500_000_000, "2.5 GB")]
    public void FormatsBytesLikeMac(long bytes, string expected)
    {
        Assert.Equal(expected, StorageCalculator.Format(bytes));
    }

    [Fact]
    public void RecentContentOrdersByInstallTime()
    {
        using var root = new TestRoot();
        root.Write("chars/old/old.def", TestRoot.CharDef("Old"));
        root.Write("chars/new/new.def", TestRoot.CharDef("New"));
        root.Write("stages/mid.def", "[Info]\nname = Middle Stage\n[BGdef]\nspr = m.sff\n");
        Directory.SetCreationTimeUtc(root.Full("chars/old"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetCreationTimeUtc(root.Full("stages/mid.def"), new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetCreationTimeUtc(root.Full("chars/new"), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var snapshot = new LibraryIndexService().Index(root.Path);
        var recent = RecentContent.FromSnapshot(snapshot);
        Assert.Equal(["New", "Middle Stage", "Old"], recent.Select(r => r.Name));
        Assert.Equal(RecentContentType.Stage, recent[1].Type);
    }

    [Theory]
    [InlineData(0.5, "Just now")]
    [InlineData(5, "5h ago")]
    [InlineData(30, "Yesterday")]
    [InlineData(24 * 3, "3d ago")]
    [InlineData(24 * 15, "2w ago")]
    [InlineData(24 * 30 * 36 + 5, "36mo ago")]
    public void RelativeAgeMatchesMacStageBrowser(double hoursAgo, string expected)
    {
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, RecentContent.FormatRelativeAge(now.AddHours(-hoursAgo), now));
    }

    [Fact]
    public void SnapshotCarriesStatusAndConfig()
    {
        using var root = new TestRoot();
        root.Write("chars/kfm/kfm.def", TestRoot.CharDef("KFM"));
        root.Write("chars/off/off.def", TestRoot.CharDef("Off"));
        root.Write("stages/s.def", "[Info]\nname = Stage S\n[BGdef]\nspr = s.sff\n");
        root.Write("data/select.def", "[Characters]\nkfm\n;off\n[ExtraStages]\nstages/s.def\n");
        root.Write("save/config.ini", "[Sound]\nMasterVolume = 70\n");

        var snapshot = new LibraryIndexService().Index(root.Path);
        Assert.Equal(ContentStatus.Active, snapshot.Characters.Single(c => c.Id == "kfm").Status);
        Assert.Equal(ContentStatus.Disabled, snapshot.Characters.Single(c => c.Id == "off").Status);
        Assert.Equal(ContentStatus.Active, snapshot.Stages.Single().Status);
        Assert.Equal(1, snapshot.ActiveCharacterCount);
        Assert.Equal(70, snapshot.Config.MasterVolume);
    }
}
