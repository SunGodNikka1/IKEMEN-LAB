using System.Text.Json;
using IKEMENLab.Cli;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Query;
using Xunit;

namespace IKEMENLab.Tests;

public class XRayQueryTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly SemanticIndex _v;

    public XRayQueryTests() => _v = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine);

    public void Dispose() => _fx.Dispose();

    private static IEnumerable<string> Names(Explanation e, string section) =>
        e.Sections.Single(s => s.Title == section).Items.Select(i => i.Id);

    // ------------------------------------------------------------------ explain

    [Fact]
    public void ExplainStateWalksFromCommandsToOutgoingStates()
    {
        var e = _v.Explain("state:10800")!;
        var entered = e.Sections.Single(s => s.Title == "Entered by").Items.Single();
        Assert.Equal("state:-1/ctrl:1", entered.Id);
        Assert.Equal(Confidence.StaticProven, entered.Confidence);
        Assert.Contains("LoveTrain", entered.Note);
        Assert.Contains("power >= 3000", entered.Note);

        Assert.Contains("cmd:LoveTrain", Names(e, "Commands that lead here"));
        Assert.Contains("cmd:D4C_alt", Names(e, "Commands that lead here"));
        Assert.Contains("state:10800/ctrl:1", Names(e, "Controllers"));
        Assert.Contains("proj:10801", Names(e, "Projectiles"));
        Assert.Contains("var:var:12", Names(e, "Variables"));
        Assert.Contains("anim:10800", Names(e, "Animation"));
        Assert.Contains("state:10800/ctrl:1/hitdef", Names(e, "HitDefs"));
        Assert.Contains("state:0", Names(e, "Leads to"));
        Assert.Contains("state:10890", Names(e, "Leads to"));
        Assert.Contains("resource:power", Names(e, "Power"));
        Assert.Contains("ability:10800", Names(e, "Abilities"));
        Assert.All(e.Sections.SelectMany(s => s.Items).Where(i => i.Source is not null), i => Assert.NotNull(_v.FileOf(i.Source)));
    }

    [Fact]
    public void ExplainVariableHelperAbilityAndCommand()
    {
        var v = _v.Explain("var:var:12")!;
        Assert.Contains("state:3000/ctrl:1", Names(v, "Written by"));
        Assert.Contains("state:340/ctrl:0", Names(v, "Read by"));
        Assert.Contains("state:10800/ctrl:2", Names(v, "Reset by"));

        var h = _v.Explain("helper:340")!;
        Assert.Contains("state:3000/ctrl:0", Names(h, "Spawned by"));
        Assert.Contains("state:340", Names(h, "Runs states"));

        var a = _v.Explain("ability:10800")!;
        Assert.Contains("cmd:LoveTrain", Names(a, "Commands"));
        Assert.Contains("proj:10801", Names(a, "Projectiles"));
        Assert.Contains("state:10890", Names(a, "Victim-state paths"));
        Assert.Contains("state:-1/ctrl:1", Names(a, "Entry gates"));

        var c = _v.Explain("cmd:D4C")!;
        Assert.Contains("state:-1/ctrl:0", Names(c, "Used by controllers"));
        Assert.Contains("state:3000", Names(c, "Leads to"));
        Assert.Null(_v.Explain("state:99999"));
    }

    // ------------------------------------------------------------------ transitions, variables, AI

    [Fact]
    public void TransitionsFromCarryTheirGates()
    {
        var t = _v.TransitionsFrom("state:200");
        var chain = t.Single(x => x.ToState == "state:210");
        Assert.Equal(Confidence.StaticProven, chain.Confidence);
        Assert.Equal("state:200/ctrl:1", chain.ControllerId);
        Assert.Contains("movecontact", chain.Gate!.TriggerAll[0].Text.ToLowerInvariant());
        Assert.Contains(t, x => x.ToState == "state:0");
        // On-hit attacker state (p1stateno) is a transition too.
        Assert.Contains(t, x => x.Kind == RelationKind.SetsAttackerState && x.ToState == "state:0");

        var into = _v.TransitionsInto("state:3000");
        Assert.Contains(into, x => x.FromState == "state:-1" && x.ControllerId == "state:-1/ctrl:0");
    }

    [Fact]
    public void VariableSpecsResolveAndUsagesListEveryAccess()
    {
        Assert.Equal("var:var:20", _v.ResolveVariableId("var(20)"));
        Assert.Equal("var:var:20", _v.ResolveVariableId("var:var:20"));
        Assert.Equal("var:var:12", _v.ResolveVariableId("VAR:12"));
        Assert.Null(_v.ResolveVariableId("var(9999)"));
        Assert.Null(_v.ResolveVariableId("nonsense"));

        var uses = _v.VarUsage("var:var:12");
        Assert.Contains(uses, u => u.Relationship.Kind == RelationKind.WritesVar && u.State!.Id == "state:3000");
        Assert.Contains(uses, u => u.Relationship.Kind == RelationKind.ReadsVar && u.Relationship.Prop("scope") == "root");
        Assert.Contains(uses, u => u.Relationship.Kind == RelationKind.ResetsVar);
    }

    [Fact]
    public void AiEntryPointsAreTheControllersThatReadAiLevel()
    {
        Assert.Equal(["state:-1/ctrl:4", "state:-1/ctrl:5"], _v.AiEntryPoints().Select(o => o.Id));
    }

    [Fact]
    public void FindByCategoryUsesInferredAbilityLabels()
    {
        // 1500 is the direct grab; 200 is a normal that can chain into the grab in state 210, so its closure contains a throw too.
        Assert.Equal(["ability:1500", "ability:200"], _v.AbilitiesLabelled("Throw").Select(a => a.Id));
        Assert.Contains("ability:10800", _v.AbilitiesLabelled("Projectile").Select(a => a.Id));
        Assert.Contains("ability:10800", _v.AbilitiesLabelled("Super").Select(a => a.Id));
        Assert.Contains(_v.Search("LoveTrain"), o => o.Id == "cmd:LoveTrain");
    }

    // ------------------------------------------------------------------ graph

    [Fact]
    public void StateGraphAggregatesEdgesWithConfidence()
    {
        var g = StateGraph.Build(_v);
        Assert.Contains(g.Outgoing("state:200"), e => e.To == "state:210" && e.Kind == StateEdgeKind.Change && e.Confidence == Confidence.StaticProven);
        Assert.Contains(g.Outgoing("state:210"), e => e.To == "state:1590" && e.Kind == StateEdgeKind.Victim);
        Assert.Contains(g.Outgoing("state:3000"), e => e.To == "state:340" && e.Kind == StateEdgeKind.HelperSpawn && e.ViaHelper == "helper:340");
        Assert.Contains(g.Outgoing("state:-1"), e => e.To.StartsWith("dynamic:") && e.Confidence == Confidence.Unknown);
        Assert.Equal(Confidence.Unknown, StateGraph.Weakest(Confidence.StaticProven, Confidence.Unknown));
    }

    [Fact]
    public void LayoutPutsIncomingLeftOutgoingRightAndIsDeterministic()
    {
        var g = StateGraph.Build(_v);
        var a = GraphLayout.Neighborhood(g, "state:200", 2);
        var b = GraphLayout.Neighborhood(g, "state:200", 2);
        Assert.Equal(a.Nodes, b.Nodes);
        var center = a.Nodes.Single(n => n.IsCenter);
        Assert.Equal("state:200", center.Id);
        Assert.Contains(a.Nodes, n => n.Id == "state:-1" && n.X < center.X);
        Assert.Contains(a.Nodes, n => n.Id == "state:210" && n.X > center.X);
        Assert.All(a.Nodes, n => Assert.True(n.Y >= 0));
        Assert.All(a.Edges, e => Assert.True(a.Nodes.Any(n => n.Id == e.From) && a.Nodes.Any(n => n.Id == e.To)));
    }

    // ------------------------------------------------------------------ JSON

    [Fact]
    public void JsonIsByteDeterministicAndVersioned()
    {
        var again = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine);
        var json = XRayJson.Index(_v);
        Assert.Equal(json, XRayJson.Index(again));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ikemenlab.xray/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("char:Valentine", doc.RootElement.GetProperty("character").GetString());
        var ids = doc.RootElement.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("id").GetString()!).ToList();
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), ids);
        Assert.DoesNotContain("RuntimeVerified", json);
    }

    [Fact]
    public void JsonMatchesTheCheckedInSchemaVocabulary()
    {
        var path = FindDoc("xray-schema-v1.json");
        Assert.NotNull(path);
        using var schema = JsonDocument.Parse(File.ReadAllText(path!));
        var defs = schema.RootElement.GetProperty("definitions");
        static List<string> Enum(JsonElement e) => e.GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(System.Enum.GetNames<Confidence>().OrderBy(x => x), Enum(defs.GetProperty("confidence")).OrderBy(x => x));
        Assert.Equal(System.Enum.GetNames<ObjectKind>().OrderBy(x => x), Enum(defs.GetProperty("objectKind")).OrderBy(x => x));
        Assert.Equal(System.Enum.GetNames<RelationKind>().OrderBy(x => x), Enum(defs.GetProperty("relationKind")).OrderBy(x => x));

        // Every emitted object/relationship has the schema's required keys.
        using var doc = JsonDocument.Parse(XRayJson.Index(_v));
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.All(required, k => Assert.True(doc.RootElement.TryGetProperty(k, out _), k));
        foreach (var o in doc.RootElement.GetProperty("objects").EnumerateArray())
            foreach (var k in new[] { "id", "kind", "name" }) Assert.True(o.TryGetProperty(k, out _));
        foreach (var r in doc.RootElement.GetProperty("relationships").EnumerateArray())
        {
            foreach (var k in new[] { "id", "kind", "from", "to", "confidence", "evidence" }) Assert.True(r.TryGetProperty(k, out _));
            Assert.True(r.GetProperty("evidence").GetArrayLength() >= 1);
            Assert.Contains(r.GetProperty("confidence").GetString(), Enum(defs.GetProperty("confidence")));
        }
    }

    private static string? FindDoc(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "docs", name);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    // ------------------------------------------------------------------ CLI

    private (int Code, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = CliApp.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private string Target => Path.Combine(_fx.Root, "chars", "Valentine");

    [Fact]
    public void CliIndexIsTheSameJsonAsTheApi()
    {
        var r = Cli("xray", "index", Target);
        Assert.Equal(0, r.Code);
        Assert.Equal(XRayJson.Index(_v).TrimEnd(), r.Out.TrimEnd());
        Assert.Equal(r.Out, Cli("xray", "index", Path.Combine(Target, "Valentine.def")).Out);
    }

    [Fact]
    public void CliAnswersTheAgentQuestions()
    {
        using var explain = JsonDocument.Parse(Cli("xray", "explain", Target, "state:10800").Out);
        Assert.Equal("state:10800", explain.RootElement.GetProperty("subject").GetProperty("id").GetString());
        Assert.Contains(explain.RootElement.GetProperty("sections").EnumerateArray(), s => s.GetProperty("title").GetString() == "Leads to");

        using var vars = JsonDocument.Parse(Cli("xray", "var", Target, "var(12)").Out);
        Assert.Equal("var:var:12", vars.RootElement.GetProperty("variable").GetString());
        Assert.True(vars.RootElement.GetProperty("uses").GetArrayLength() >= 3);

        using var helper = JsonDocument.Parse(Cli("xray", "helper", Target, "340").Out);
        Assert.Equal("helper:340", helper.RootElement.GetProperty("subject").GetProperty("id").GetString());

        using var ai = JsonDocument.Parse(Cli("xray", "ai-entry", Target).Out);
        Assert.Equal(2, ai.RootElement.GetProperty("results").GetArrayLength());

        using var throws = JsonDocument.Parse(Cli("xray", "find", Target, "throws").Out);
        Assert.Equal("ability:1500", throws.RootElement.GetProperty("results")[0].GetProperty("id").GetString());
        Assert.Equal("Inferred", throws.RootElement.GetProperty("results")[0].GetProperty("labels")[0].GetProperty("confidence").GetString());

        using var trans = JsonDocument.Parse(Cli("xray", "transitions", Target, "200").Out);
        Assert.Contains(trans.RootElement.GetProperty("transitions").EnumerateArray(), t => t.GetProperty("to").GetString() == "state:210");

        using var search = JsonDocument.Parse(Cli("xray", "search", Target, "love").Out);
        Assert.NotEmpty(search.RootElement.GetProperty("results").EnumerateArray());

        using var rules = JsonDocument.Parse(Cli("xray", "rules").Out);
        Assert.True(rules.RootElement.GetProperty("rules").GetArrayLength() > 40);
    }

    [Fact]
    public void CliReportsErrorsWithExitCodes()
    {
        Assert.Equal(2, Cli().Code);
        Assert.Equal(2, Cli("xray", "explain", Target).Code);
        Assert.Equal(3, Cli("xray", "explain", Target, "state:99999").Code);
        Assert.Equal(3, Cli("xray", "var", Target, "var(9999)").Code);
        Assert.Equal(3, Cli("xray", "index", Path.Combine(_fx.Root, "nowhere")).Code);
        var combos = Cli("xray", "combos", Target);
        Assert.Equal(2, combos.Code);
        Assert.Contains("milestone 2", combos.Err);
        using var err = JsonDocument.Parse(combos.Out);
        Assert.True(err.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void CliTextModeIsReadable()
    {
        var text = Cli("xray", "explain", Target, "state:10800", "--text").Out;
        Assert.Contains("Entered by", text);
        Assert.Contains("[StaticProven]", text);
        Assert.Contains("Valentine.cns:", text);
    }
}
