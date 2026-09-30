using System.Text.RegularExpressions;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// The runtime probe talks to the engine over Lua, and the names it uses have to be the ones the engine
/// actually binds. IKEMEN registers camelCase globals (<c>src/script.go</c>: <c>luaRegister(l, "stateNo", …)</c>),
/// not the MUGEN System-script spellings such as <c>stateno</c>. Lua is case sensitive, so guessing the MUGEN
/// name silently yields null — which is exactly why the first real spike run reported <c>p1.state</c>
/// unsupported on a build that exposes it. These tests pin the contract so the names cannot drift back.
/// </summary>
public class XRayProbeTests
{
    /// <summary>Trace field, and the engine binding the probe must try first to fill it.</summary>
    private static readonly (string Field, string Primary)[] Required =
    [
        ("\"state\"", "stateNo"),
        ("\"prevState\"", "prevStateNo"),
        ("\"stateType\"", "stateType"),
        ("\"moveType\"", "moveType"),
        ("\"velX\"", "velX"),
        ("\"velY\"", "velY"),
        ("\"animElem\"", "animElemNo"),
        ("\"moveHit\"", "moveHit"),
        ("\"moveContact\"", "moveContact"),
    ];

    private static string ProbeText()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "windows", "IKEMENLab.Core", "XRay", "Runtime", "xray_probe.lua");
            if (File.Exists(p)) return File.ReadAllText(p);
        }

        throw new FileNotFoundException("xray_probe.lua not found above the test output directory.");
    }

    [Fact]
    public void EveryPlayerFieldTriesTheEngineBindingBeforeAnyMugenSpelling()
    {
        var text = ProbeText();
        foreach (var (field, primary) in Required)
        {
            // The FIELDS row, e.g. { "state", num, { function() return call("stateNo") end, …
            var row = Regex.Match(text, @"\{ " + Regex.Escape(field) + @",.*");
            Assert.True(row.Success, $"probe has no FIELDS row for {field}");

            var firstCall = Regex.Match(row.Value, @"call\(""(?<name>[^""]+)""");
            Assert.True(firstCall.Success, $"no call() in the {field} row");
            Assert.Equal(primary, firstCall.Groups["name"].Value);
        }
    }

    [Fact]
    public void TheProbeKeepsRawEngineFactsOnlyAndNoInterpretation()
    {
        var text = ProbeText();
        // Naming an outcome or a judgement is interpretation and belongs to a later milestone.
        foreach (var forbidden in new[] { "combocount", "hitcount", "antiair", "anti_air", "isHit", "wasHit" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WithPlayerSetsTheEngineContextBeforeAnyFieldIsRead()
    {
        // The engine's Lua API is redirect based: player(n) sets the context that the field getters then read.
        // Without it every "per-player" value would really be whichever character happened to run last.
        var text = ProbeText();
        var withPlayer = Regex.Match(text, @"local function withPlayer\(.*?\nend", RegexOptions.Singleline);
        Assert.True(withPlayer.Success, "withPlayer is missing");
        Assert.Contains("G(\"player\")", withPlayer.Value);
    }
}
