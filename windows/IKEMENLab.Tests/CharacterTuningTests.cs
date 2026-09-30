using System.Text;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using Xunit;

namespace IKEMENLab.Tests;

public class CharacterTuningTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tuning-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _cns;
    private readonly CharacterTuningService _service;
    private readonly CharacterEntry _entry;

    private const string CnsText =
        "; Test\r\n[Data]\r\nlife = 1000 ; hp\r\npower = 3000\r\nattack = 110\r\n\r\n[Size]\r\nxscale = 0.8\r\nyscale = 0.8\r\n\r\n[Statedef 0]\r\ntype = S\r\n";

    public CharacterTuningTests()
    {
        _root = Path.Combine(_tmp, "ikemen");
        foreach (var d in new[] { "chars", "stages", "data", "save" }) Directory.CreateDirectory(Path.Combine(_root, d));
        File.WriteAllText(Path.Combine(_root, "data", "select.def"), "; roster\n");
        var dir = Path.Combine(_root, "chars", "Hero");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Hero.def"), "[Info]\nname = Hero\n[Files]\ncns = Hero.cns\nsprite = Hero.sff\n");
        _cns = Path.Combine(dir, "Hero.cns");
        File.WriteAllBytes(_cns, Encoding.Latin1.GetBytes(CnsText));
        _entry = new CharacterEntry
        {
            Id = "Hero", DisplayName = "Hero", Name = "Hero", Author = "t", VersionDate = "",
            DefPath = "chars/Hero/Hero.def", FolderPath = "chars/Hero"
        };
        _service = new CharacterTuningService(new SafeMutationService(Path.Combine(_tmp, "ops"), Path.Combine(_tmp, "bak")),
            Path.Combine(_tmp, "stg"));
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private TuningSnapshot Load() => _service.Load(_root, _entry)!;

    [Fact]
    public void LoadReadsWrittenValuesAndFlagsEngineDefaults()
    {
        var s = Load();
        Assert.Equal(1000, s["Data.life"].Current);
        Assert.False(s["Data.life"].IsEngineDefault);
        Assert.Equal(0.8, s["Size.xscale"].Current, 6);
        Assert.True(s["Data.defence"].IsEngineDefault);
        Assert.Equal(100, s["Data.defence"].Current);
    }

    [Fact]
    public void MissingCnsGivesNoSnapshot()
    {
        File.Delete(_cns);
        Assert.Null(_service.Load(_root, _entry));
    }

    [Theory]
    [InlineData("Data.life", "0", "between")]
    [InlineData("Data.life", "abc", "number")]
    [InlineData("Data.life", "12.5", "whole")]
    [InlineData("Data.defence", "0", "between")]
    [InlineData("Size.xscale", "-1", "between")]
    [InlineData("Size.xscale", "NaN", "number")]
    [InlineData("Size.nonsense", "1", "Unknown")]
    public void InvalidValuesAreRejectedWithAReason(string id, string value, string reason)
    {
        var plan = _service.Plan(Load(), [new TuningRequest(id, value)]);
        Assert.False(plan.CanApply);
        Assert.Contains(reason, plan.Errors.Values.Single());
    }

    [Fact]
    public void UnchangedValuesProduceNoChange()
    {
        var plan = _service.Plan(Load(), [new TuningRequest("Data.life", "1000"), new TuningRequest("Data.defence", "100")]);
        Assert.Empty(plan.Changes);
        var before = File.ReadAllBytes(_cns);
        var result = _service.Apply(_root, Load(), [new TuningRequest("Data.life", "1000")]);
        Assert.True(result.Success);
        Assert.Equal(before, File.ReadAllBytes(_cns));
    }

    [Fact]
    public void ApplyChangesOnlyTheEditedLines_AndKeepsNonAsciiBytes()
    {
        var bytes = Encoding.Latin1.GetBytes(CnsText.Replace("; Test", "; Café Hero"));
        File.WriteAllBytes(_cns, bytes);
        var result = _service.Apply(_root, Load(), [new TuningRequest("Data.life", "1400"), new TuningRequest("Data.defence", "120")]);
        Assert.True(result.Success, result.Error);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(_cns));
        Assert.Contains("life = 1400 ; hp\r\n", text);
        Assert.Contains("attack = 110\r\ndefence = 120\r\n\r\n[Size]", text);   // new key goes at the end of [Data]
        Assert.StartsWith("; Café Hero\r\n", text);
        var after = Load();
        Assert.Equal(1400, after["Data.life"].Current);
        Assert.Equal(120, after["Data.defence"].Current);
    }

    [Fact]
    public void ScaleSizeMultipliesBothScales()
    {
        var snapshot = Load();
        var result = _service.Apply(_root, snapshot, CharacterTuningService.ScaleSize(snapshot, 1.25));
        Assert.True(result.Success, result.Error);
        Assert.Equal(1.0, Load()["Size.xscale"].Current, 6);
        Assert.Equal(1.0, Load()["Size.yscale"].Current, 6);
    }

    [Fact]
    public void ScaleFromAnAbsentKeyStartsAtTheEngineDefault()
    {
        File.WriteAllBytes(_cns, Encoding.Latin1.GetBytes("[Data]\nlife = 1000\n[Statedef 0]\n"));
        var snapshot = Load();
        var result = _service.Apply(_root, snapshot, CharacterTuningService.ScaleSize(snapshot, 1.5));
        Assert.True(result.Success, result.Error);
        var s = Load();
        Assert.Equal(1.5, s["Size.xscale"].Current, 6);
        Assert.False(s["Size.xscale"].IsEngineDefault);
    }

    [Fact]
    public void UndoRestoresTheOriginalBytes()
    {
        var original = File.ReadAllBytes(_cns);
        var result = _service.Apply(_root, Load(), [new TuningRequest("Data.life", "1800")]);
        Assert.True(result.Success, result.Error);
        Assert.NotEqual(original, File.ReadAllBytes(_cns));
        Assert.True(_service.Undo(result.OperationId!).Success);
        Assert.Equal(original, File.ReadAllBytes(_cns));
    }

    [Fact]
    public void ApplyRefusesWhenTheFileChangedAfterLoading()
    {
        var snapshot = Load();
        File.WriteAllBytes(_cns, Encoding.Latin1.GetBytes(CnsText.Replace("life = 1000", "life = 900")));
        var result = _service.Apply(_root, snapshot, [new TuningRequest("Data.life", "1500")]);
        Assert.False(result.Success);
        Assert.Contains("changed", result.Error);
        Assert.Contains("life = 900", File.ReadAllText(_cns));
    }

    [Fact]
    public void ApplyIsRejectedWhenAnyValueIsInvalid_AndWritesNothing()
    {
        var before = File.ReadAllBytes(_cns);
        var result = _service.Apply(_root, Load(), [new TuningRequest("Data.life", "1500"), new TuningRequest("Size.xscale", "99")]);
        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllBytes(_cns));
    }
}
