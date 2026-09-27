using IKEMENLab.Core.Services;
using Xunit;

namespace IKEMENLab.Tests;

public class GameLauncherTests
{
    [Fact]
    public void CanLaunchRequiresExe()
    {
        var root = Path.Combine(Path.GetTempPath(), "ikemenlab-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chars"));
        Directory.CreateDirectory(Path.Combine(root, "stages"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            var launcher = new GameLauncher();
            Assert.False(launcher.CanLaunch(root));
            File.WriteAllBytes(Path.Combine(root, "Ikemen_GO.exe"), [0x4D, 0x5A]);
            Assert.True(launcher.CanLaunch(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LaunchFailsGracefullyWhenMissing()
    {
        var result = new GameLauncher().Launch(Path.Combine(Path.GetTempPath(), "no-ikemen-" + Guid.NewGuid()));
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}
