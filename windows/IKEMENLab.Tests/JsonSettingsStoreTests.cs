using IKEMENLab.Core.Settings;
using Xunit;

namespace IKEMENLab.Tests;

public class JsonSettingsStoreTests
{
    [Fact]
    public void RoundTripAtomic()
    {
        var path = Path.Combine(Path.GetTempPath(), "ikemenlab-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            store.Save(new AppSettings { IkemenRoot = @"D:\Games 3\Ikemen_GO-v1.0.0" });
            var loaded = store.Load();
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(@"D:\Games 3\Ikemen_GO-v1.0.0", loaded.IkemenRoot);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
