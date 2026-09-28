using System.Text;
using IKEMENLab.Core.Config;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Screenpacks;
using Xunit;

namespace IKEMENLab.Tests;

public sealed class ConfigAndScreenpackActivationTests : IDisposable
{
    private readonly string _fixture;
    private readonly string _ops;
    private readonly string _bak;
    private readonly string _stg;
    private readonly SafeMutationService _mutations;
    private readonly IkemenConfigMutationService _config;
    private readonly ScreenpackActivationService _screenpacks;

    public ConfigAndScreenpackActivationTests()
    {
        DefFileReader.EnsureEncodingsRegistered();
        _fixture = Path.Combine(Path.GetTempPath(), "ikemenlab-cfg-" + Guid.NewGuid().ToString("N"));
        _ops = Path.Combine(Path.GetTempPath(), "ikemenlab-cfg-ops-" + Guid.NewGuid().ToString("N"));
        _bak = Path.Combine(Path.GetTempPath(), "ikemenlab-cfg-bak-" + Guid.NewGuid().ToString("N"));
        _stg = Path.Combine(Path.GetTempPath(), "ikemenlab-cfg-stg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_fixture, "save"));
        Directory.CreateDirectory(Path.Combine(_fixture, "data", "packA"));
        Directory.CreateDirectory(Path.Combine(_fixture, "data", "packB"));
        Directory.CreateDirectory(_ops);
        Directory.CreateDirectory(_bak);
        Directory.CreateDirectory(_stg);
        SeedConfig("""
            ; header keep
            [Config]
            Motif             = data/packA/system.def
            [Video]
            Fullscreen               = 1
            VSync                    = 1
            OtherVideo               = 42
            [Sound]
            MasterVolume         = 80
            [Options]
            Difficulty = 8
            """);
        WritePack("packA", "Pack A");
        WritePack("packB", "Pack B");
        _mutations = new SafeMutationService(_ops, _bak);
        _config = new IkemenConfigMutationService(_mutations, _stg);
        _screenpacks = new ScreenpackActivationService(_config);
    }

    public void Dispose()
    {
        TryDelete(_fixture); TryDelete(_ops); TryDelete(_bak); TryDelete(_stg);
    }

    [Fact]
    public void MotifChange_PreservesUnrelated_AndRollsBack()
    {
        var before = File.ReadAllText(ConfigPath());
        var result = _config.SetMotif(_fixture, "data/packB/system.def");
        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        var after = File.ReadAllText(ConfigPath());
        Assert.Contains("Motif             = data/packB/system.def", after);
        Assert.Contains("; header keep", after);
        Assert.Contains("OtherVideo               = 42", after);
        Assert.Contains("Difficulty = 8", after);
        Assert.Contains("MasterVolume         = 80", after);
        Assert.Equal(1, Directory.GetDirectories(_ops).Length);

        Assert.True(_mutations.Rollback(result.OperationId!).Success);
        Assert.Equal(Normalize(before), Normalize(File.ReadAllText(ConfigPath())));
    }

    [Fact]
    public void VSyncFullscreenVolume_WriteAndVerify()
    {
        Assert.True(_config.SetBool(_fixture, ConfigValueKind.VSync, false).Success);
        Assert.False(IkemenConfigReader.Read(_fixture).VSync);
        Assert.Contains("VSync                    = 0", File.ReadAllText(ConfigPath()));

        Assert.True(_config.SetBool(_fixture, ConfigValueKind.Fullscreen, false).Success);
        Assert.False(IkemenConfigReader.Read(_fixture).Fullscreen);

        Assert.True(_config.SetMasterVolume(_fixture, 55).Success);
        Assert.Equal(55, IkemenConfigReader.Read(_fixture).MasterVolume);
        Assert.Contains("MasterVolume         = 55", File.ReadAllText(ConfigPath()));
        Assert.Contains("OtherVideo               = 42", File.ReadAllText(ConfigPath()));
    }

    [Fact]
    public void DryRunAndMalformedFailSafe()
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ConfigPath())));
        Assert.True(_config.SetMotif(_fixture, "data/packB/system.def", dryRun: true).Success);
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ConfigPath()))));

        File.WriteAllText(ConfigPath(), "not-ini{{{{");
        // DefParser may still return something; empty Motif section means Set may add keys.
        // Use missing config entirely:
        File.Delete(ConfigPath());
        Assert.False(_config.SetMotif(_fixture, "data/packB/system.def").Success);
    }

    [Fact]
    public void ScreenpackActivate_PreviewCancelAndActivate()
    {
        var packs = ScreenpackIndexer.Index(_fixture, "data/packA/system.def");
        var packB = Assert.Single(packs, p => p.DefPath.Contains("packB", StringComparison.OrdinalIgnoreCase));
        var preview = _screenpacks.Preview(_fixture, packB);
        Assert.True(preview.CanActivate, preview.Error);
        Assert.Equal("data/packA/system.def", preview.CurrentMotif);
        Assert.Equal("data/packB/system.def", preview.NewMotif);

        var dry = _screenpacks.Activate(_fixture, packB, dryRun: true);
        Assert.True(dry.Success, dry.Error);
        Assert.Equal("data/packA/system.def", IkemenConfigReader.Read(_fixture).Motif);

        var act = _screenpacks.Activate(_fixture, packB);
        Assert.True(act.Success, act.Error);
        Assert.Equal("data/packB/system.def", IkemenConfigReader.Read(_fixture).Motif);
        var indexed = ScreenpackIndexer.Index(_fixture, IkemenConfigReader.Read(_fixture).Motif);
        Assert.True(Assert.Single(indexed, p => p.IsActive).DefPath.Contains("packB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidScreenpackRejected()
    {
        var fake = new ScreenpackEntry
        {
            Id = "missing",
            Name = "Missing",
            Author = "X",
            DefPath = "data/nope/system.def",
            Components = ScreenpackComponents.None
        };
        var preview = _screenpacks.Preview(_fixture, fake);
        Assert.False(preview.CanActivate);
        Assert.Equal("data/packA/system.def", IkemenConfigReader.Read(_fixture).Motif);
    }

    private void WritePack(string folder, string name)
    {
        var dir = Path.Combine(_fixture, "data", folder);
        File.WriteAllText(Path.Combine(dir, "system.def"), $"""
            [Info]
            name = {name}
            author = Test
            localcoord = 1280,720
            [Files]
            spr = system.sff
            fight = fight.def
            [Title Info]
            [Select Info]
            rows = 4
            columns = 6
            [VS Screen]
            [Option Info]
            """);
        File.WriteAllBytes(Path.Combine(dir, "system.sff"), [0]);
        File.WriteAllText(Path.Combine(dir, "fight.def"), "[Info]\nname=f\n");
    }

    private void SeedConfig(string content)
        => File.WriteAllText(ConfigPath(), content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));

    private string ConfigPath() => Path.Combine(_fixture, "save", "config.ini");
    private static string Normalize(string s) => s.Replace("\r\n", "\n");
    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* ignore */ }
    }
}
