using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public class InstallationValidatorTests
{
    [Fact]
    public void ReportsMissingPieces()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ikemenlab-inst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "chars"));
            var check = new IkemenInstallationValidator().Validate(dir);
            Assert.True(check.CharsPresent);
            Assert.False(check.StagesPresent);
            Assert.False(check.DataPresent);
            Assert.False(check.ExePresent);
            Assert.False(check.CanBrowse);
            Assert.False(check.CanLaunch);
            Assert.Contains("stages/", check.MissingItems);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BrowseWithoutExe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ikemenlab-inst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "chars"));
        Directory.CreateDirectory(Path.Combine(dir, "stages"));
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        try
        {
            var check = new IkemenInstallationValidator().Validate(dir);
            Assert.True(check.CanBrowse);
            Assert.False(check.CanLaunch);
        }
        finally { Directory.Delete(dir, true); }
    }
}
