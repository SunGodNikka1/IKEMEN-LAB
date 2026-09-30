using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>Source-state constraints added to the gate facets for Milestone 2.</summary>
public class XRayGateStateConstraintTests
{
    private static GateFacets Facets(string trigger)
    {
        var line = new TriggerLine(trigger, ExprParser.Parse(trigger), null);
        return GateAnalyzer.Expand([], [line]).Single();
    }

    [Theory]
    [InlineData("stateno = 200", 200, 200)]
    [InlineData("StateNo = [200, 210]", 200, 210)]
    [InlineData("stateno = (200, 210]", 201, 210)]
    [InlineData("stateno = [200, 210)", 200, 209)]
    [InlineData("stateno >= 1000", 1000, int.MaxValue)]
    [InlineData("stateno > 1000", 1001, int.MaxValue)]
    [InlineData("stateno <= 50", int.MinValue, 50)]
    public void SourceStateShapesAreRecognised(string trigger, int lo, int hi)
    {
        var f = Facets(trigger);
        Assert.Equal([new StateRange(lo, hi)], f.SourceStateRanges);
        Assert.Equal(0, f.OtherConditions);
    }

    [Fact]
    public void OrOfEqualitiesIsAUnion_AndAndIntersects()
    {
        Assert.Equal([new StateRange(200, 200), new StateRange(300, 300)], Facets("stateno = 200 || stateno = 300").SourceStateRanges);
        Assert.Equal([new StateRange(210, 250)], Facets("stateno >= 210 && stateno = [200,250]").SourceStateRanges);
        Assert.Empty(Facets("stateno = 200 && stateno = 300").SourceStateRanges is { Count: > 0 } r ? r : []);
    }

    [Fact]
    public void NegationsAndPrevStateAreKeptSeparately()
    {
        var f = Facets("stateno != 200 && prevstateno = 1000 && movehit");
        Assert.Equal([new StateRange(200, 200)], f.ExcludedStateRanges);
        Assert.Equal([new StateRange(1000, 1000)], f.PrevStateRanges);
        Assert.Empty(f.SourceStateRanges);
        Assert.Contains("movehit", f.Contact);
    }

    [Fact]
    public void NonLiteralStateTestsStayUnmodelledAndAreListed()
    {
        var f = Facets("stateno = var(3) && movehit");
        Assert.Empty(f.SourceStateRanges);
        Assert.Equal(1, f.OtherConditions);
        Assert.Equal(["(= stateno (var 3))"], f.UnmodelledConditions);
    }
}

public class XRayComboTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly SemanticIndex _index;
    private readonly CandidateGraph _g;

    public XRayComboTests()
    {
        _index = CharacterSemanticIndexer.Build(_fx.Root, _fx.ComboGuy);
        _g = CandidateGraph.Build(_index);
    }

    public void Dispose() => _fx.Dispose();

    private CandidateEdge Edge(string from, string to, EdgeKind? kind = null, string? controllerContains = null) =>
        _g.Edges.Single(e => e.From == from && e.To == to && (kind is null || e.Kind == kind) &&
                             (controllerContains is null || _index.Get(e.ControllerId)!.Name.Contains(controllerContains, StringComparison.Ordinal)));

    private static List<string> Seq(ComboRoute r) => r.Steps.Select(s => s.Move.StateId.Replace("state:", "")).ToList();

    // ------------------------------------------------------------------ readiness of the index for candidate generation

    [Fact]
    public void MovesCarryTheirLiteralHitDamageCostAndTiming()
    {
        var m200 = _g.Move("state:200")!;
        Assert.Equal((1, 20.0, true, 8), (m200.HitDefCount, m200.Damage, m200.DamageExact, m200.PauseTime));
        Assert.Equal(12, m200.AnimTicks);
        Assert.Equal(3, m200.FirstHitTick);                    // AnimElem = 2 → the second frame starts at tick 3
        Assert.Equal(1000, _g.Move("state:3000")!.PowerCost);
        Assert.Equal(200, _g.Move("state:3000")!.Damage);

        var multi = _g.Move("state:300")!;
        Assert.Equal((2, 20.0, false), (multi.HitDefCount, multi.Damage, multi.DamageExact));   // lower bound
        Assert.Null(_g.Move("state:400")!.Damage);                                               // damage = var(1)
        Assert.True(_g.Move("state:0")!.IsNeutral);
        Assert.True(_g.Move("state:20")!.IsNeutral);
        Assert.False(_g.Move("state:200")!.IsNeutral);
    }

    [Fact]
    public void ReadinessReportsWhatTheGraphCannotSay()
    {
        var r = _g.Readiness();
        Assert.True(r.Edges > 10);
        Assert.Equal(1, r.DynamicTargets);
        Assert.True(r.EdgesWithUnmodelled >= 1);
        Assert.True(r.UnconstrainedGlobalEdges > 0);
        Assert.Contains(r.Findings, f => f.Contains("non-constant target"));
        Assert.Contains(r.Findings, f => f.Contains("Not modelled anywhere"));
        Assert.True(r.EdgesByKind.ContainsKey("Start") && r.EdgesByKind.ContainsKey("Cancel") && r.EdgesByKind.ContainsKey("OnHit"));
    }

    // ------------------------------------------------------------------ edges

    [Fact]
    public void NeutralStartsComeFromCommandsAndAreInferred()
    {
        var start = Edge("neutral", "state:200", EdgeKind.Start);
        Assert.Equal(["x"], start.Commands);
        Assert.True(start.Facets.CtrlRequired);
        Assert.Equal(Confidence.Inferred, start.Confidence);                      // neutral is a heuristic node
        Assert.Contains(start.Evidence, e => e.RuleId == "combo.candidate-start");
        Assert.Contains(start.Evidence, e => e.RuleId == "state.literal-change");   // the underlying fact is still cited
        Assert.Contains(_g.Edges, e => e.From == "neutral" && e.To == "state:1000" && e.Kind == EdgeKind.Start);
    }

    [Fact]
    public void LiteralSourceStateGatesExpandExactly_AndStayProven()
    {
        var cancel = Edge("state:200", "state:210", EdgeKind.Cancel, "x into y");
        Assert.Equal((ContactRequirement.Hit, Confidence.StaticProven), (cancel.Contact, cancel.Confidence));
        Assert.Equal(["y"], cancel.Commands);
        Assert.True(cancel.IsGlobal);
        Assert.Contains(cancel.Evidence, e => e.RuleId == "combo.source-literal");

        // stateno = [200,210] expands to both states, and only those.
        var specials = _g.Edges.Where(e => e.To == "state:1000" && e.Kind == EdgeKind.Cancel && _index.Get(e.ControllerId)!.Name.Contains("special cancel")).ToList();
        Assert.Equal(["state:200", "state:210"], specials.Select(e => e.From).OrderBy(x => x));
        Assert.All(specials, e => Assert.Equal((ContactRequirement.Contact, Confidence.StaticProven), (e.Contact, e.Confidence)));
    }

    [Fact]
    public void UnconstrainedGlobalGatesExpandToAttackersAndAreInferred()
    {
        var z = _g.Edges.Where(e => e.To == "state:220" && e.Kind == EdgeKind.Cancel && e.Contact == ContactRequirement.Hit).ToList();
        Assert.Contains(z, e => e.From == "state:200");
        Assert.Contains(z, e => e.From == "state:1001");
        Assert.DoesNotContain(z, e => e.From == "state:1002");                   // no HitDef, so it cannot be the source of a hit cancel
        Assert.All(z, e => Assert.Equal(Confidence.Inferred, e.Confidence));
        Assert.All(z, e => Assert.Contains(e.Evidence, x => x.RuleId == "combo.source-unconstrained"));
    }

    [Fact]
    public void ChainWhiffCancelLinkOnHitAndRecoveryAreDistinguished()
    {
        var chain = Edge("state:1000", "state:1001", EdgeKind.Chain);
        Assert.Equal(ContactRequirement.None, chain.Contact);
        Assert.Empty(chain.Commands);
        Assert.Equal(6, chain.EarliestTick);                                        // Time >= 6
        Assert.Equal(Confidence.StaticProven, chain.Confidence);

        var whiff = Edge("state:200", "state:200", EdgeKind.Cancel, "whiff repeat");
        Assert.Equal(ContactRequirement.None, whiff.Contact);
        Assert.Contains(whiff.Notes, n => n.Contains("cancels on whiff"));

        var link = Edge("state:210", "state:200", EdgeKind.Link);
        Assert.True(link.Facets.CtrlRequired);

        var onHit = Edge("state:1000", "state:1002", EdgeKind.OnHit);
        Assert.Equal(ContactRequirement.Hit, onHit.Contact);
        Assert.Contains(onHit.Evidence, e => e.RuleId == "combo.candidate-onhit");

        Assert.All(_g.Edges.Where(e => e.Kind == EdgeKind.Recovery), e => Assert.Equal("neutral", e.To));
        Assert.Contains(_g.Edges, e => e.From == "state:200" && e.Kind == EdgeKind.Recovery);
    }

    [Fact]
    public void PowerGatesAndUnmodelledConditionsTravelWithTheEdge()
    {
        var super = Edge("state:1000", "state:3000", EdgeKind.Cancel);
        Assert.Equal([new NumCompare(">=", 1000)], super.Facets.Power);
        Assert.Equal(0, super.Unmodelled.Count);

        var weird = Edge("state:210", "state:300", EdgeKind.Cancel);
        Assert.Single(weird.Unmodelled);
        Assert.Contains("random", weird.Unmodelled[0]);
        Assert.Equal(Confidence.StaticProven, weird.Confidence);                    // proven edge, honestly incomplete
    }

    [Fact]
    public void DynamicTargetsAreListedNotGuessed()
    {
        Assert.Single(_g.DynamicTargets);
        Assert.DoesNotContain(_g.Edges, e => e.ControllerId == _g.DynamicTargets[0]);
    }

    [Fact]
    public void EveryEdgeCitesRegisteredRulesAndNothingClaimsRuntimeEvidence()
    {
        foreach (var e in _g.Edges)
        {
            Assert.NotEmpty(e.Evidence);
            Assert.All(e.Evidence, x => Assert.True(EvidenceRules.Exists(x.RuleId), x.RuleId));
            Assert.NotEqual(Confidence.RuntimeVerified, e.Confidence);
            Assert.NotEqual(Confidence.Unknown, e.Confidence);
            Assert.NotNull(_index.Get(e.ControllerId));
            Assert.NotNull(_index.Relationships.SingleOrDefault(r => r.Id == e.RelationshipId));
        }
    }

    // ------------------------------------------------------------------ search

    [Fact]
    public void DefaultSearchFindsTheHandComputedStringAndOnlyCancelsWithinConstraints()
    {
        var result = ComboSearch.Find(_g, new ComboOptions { Top = 200 });
        Assert.False(result.Truncated);
        var routes = result.Routes;

        var full = routes.Single(r => Seq(r).SequenceEqual(["200", "210", "1000", "1001"]));
        Assert.Equal(120, full.DamageKnown);                                         // 20 + 30 + 50 + 20
        Assert.True(full.DamageComplete);
        Assert.Equal(0, full.MeterSpent);
        Assert.Equal(Confidence.Inferred, full.Confidence);                           // starts from the inferred neutral node
        Assert.Equal("neutral", full.StartState);
        Assert.Equal([EdgeKind.Start, EdgeKind.Cancel, EdgeKind.Cancel, EdgeKind.Chain], full.Steps.Select(s => s.Edge.Kind));

        Assert.DoesNotContain(routes, r => r.States.Contains("state:3000"));          // no meter
        Assert.DoesNotContain(routes, r => r.Steps.Any(s => s.Edge.Kind is EdgeKind.Link or EdgeKind.Recovery));
        Assert.DoesNotContain(routes, r => r.Steps.Any(s => s.Edge.Kind == EdgeKind.Cancel && s.Edge.Contact == ContactRequirement.None));
        Assert.All(routes, r => Assert.Contains("not modelled", string.Join(" ", result.Warnings)));
        Assert.All(routes, r => Assert.Contains(r.Notes, n => n.Contains("not a verified combo")));
        Assert.All(routes, r => Assert.NotEqual(Confidence.RuntimeVerified, r.Confidence));
    }

    [Fact]
    public void MeterUnlocksTheSuperAndTheCostIsSpent()
    {
        var without = ComboSearch.Find(_g, new ComboOptions { Top = 500, MaxMoves = 4 });
        var with = ComboSearch.Find(_g, new ComboOptions { Top = 500, MaxMoves = 4, StartMeter = 1000 });
        Assert.DoesNotContain(without.Routes, r => r.States.Contains("state:3000"));

        var super = with.Routes.First(r => Seq(r).SequenceEqual(["200", "1000", "3000"]));
        Assert.Equal(270, super.DamageKnown);                                         // 20 + 50 + 200
        Assert.Equal((1000, 0), (super.MeterSpent, super.EndMeter));
        Assert.True(with.Routes.Max(r => r.DamageKnown) > without.Routes.Max(r => r.DamageKnown));

        var half = ComboSearch.Find(_g, new ComboOptions { Top = 500, StartMeter = 999 });
        Assert.DoesNotContain(half.Routes, r => r.States.Contains("state:3000"));       // gate is >= 1000
    }

    [Fact]
    public void MaxMovesRepeatsAndUnmodelledFiltersApply()
    {
        var two = ComboSearch.Find(_g, new ComboOptions { Top = 500, MaxMoves = 2 });
        Assert.All(two.Routes, r => Assert.True(r.Steps.Count(s => s.Edge.Kind is EdgeKind.Start or EdgeKind.Cancel or EdgeKind.Link) <= 2));

        var clean = ComboSearch.Find(_g, new ComboOptions { Top = 500, MaxUnmodelledPerEdge = 0 });
        Assert.DoesNotContain(clean.Routes, r => r.States.Contains("state:300"));      // reached only through the gate with an unmodelled condition
        var all = ComboSearch.Find(_g, new ComboOptions { Top = 500 });
        var toThree = all.Routes.First(r => Seq(r).SequenceEqual(["200", "210", "300"]));
        Assert.Equal(70, toThree.DamageKnown);                                          // 20 + 30 + (10+10)
        Assert.False(toThree.DamageComplete);                                           // two HitDefs → a lower bound, said so
        Assert.Equal(1, toThree.UnmodelledCount);

        var twice = ComboSearch.Find(_g, new ComboOptions { Top = 2000, MaxRepeats = 2, HitConfirmOnly = false });
        Assert.Contains(twice.Routes, r => Seq(r).Take(2).SequenceEqual(["200", "200"]));
        Assert.DoesNotContain(all.Routes, r => Seq(r).GroupBy(x => x).Any(g => g.Count() > 1));
    }

    [Fact]
    public void StartingFromAStateFollowsOnlyItsOutgoingEdges()
    {
        var result = ComboSearch.Find(_g, new ComboOptions { From = "1000", Top = 50, StartMeter = 1000 });
        Assert.All(result.Routes, r => Assert.Equal("state:1000", r.StartState));
        Assert.Contains(result.Routes, r => Seq(r).SequenceEqual(["3000"]) && r.DamageKnown == 250);   // 50 (start) + 200
        Assert.Contains(result.Routes, r => Seq(r).SequenceEqual(["1001"]));
        var unknown = ComboSearch.Find(_g, new ComboOptions { From = "9999" });
        Assert.Empty(unknown.Routes);
        Assert.Contains(unknown.Warnings, w => w.Contains("not a defined state"));
    }

    [Fact]
    public void FrameEstimateUsesGateTicksAndSaysWhenItIsALowerBound()
    {
        var r = ComboSearch.Find(_g, new ComboOptions { From = "1000", Top = 10 }).Routes.First(x => Seq(x).SequenceEqual(["1001"]));
        Assert.Equal(6, r.Steps[0].Edge.EarliestTick);
        Assert.NotNull(r.MinFrames);
        Assert.Contains(r.Notes, n => n.Contains("lower bound") || r.FramesComplete);

        var capped = ComboSearch.Find(_g, new ComboOptions { Top = 500, MaxFrames = 1 });
        Assert.All(capped.Routes, x => Assert.True(x.MinFrames <= 1 || !x.FramesComplete));
    }

    [Fact]
    public void AllStrategiesAreDeterministic_AndAgreeOnTheBestKnownRoute()
    {
        foreach (var strategy in Enum.GetValues<ComboStrategy>())
        {
            var o = new ComboOptions { Strategy = strategy, Top = 30 };
            var a = ComboSearch.Find(_g, o);
            var b = ComboSearch.Find(CandidateGraph.Build(CharacterSemanticIndexer.Build(_fx.Root, _fx.ComboGuy)), o);
            Assert.Equal(a.Routes.Select(r => r.Key), b.Routes.Select(r => r.Key));
            Assert.Equal(a.Expansions, b.Expansions);
        }

        var dfs = ComboSearch.Find(_g, new ComboOptions { Strategy = ComboStrategy.Dfs, Top = 1 }).Routes[0];
        var best = ComboSearch.Find(_g, new ComboOptions { Strategy = ComboStrategy.BestFirst, Top = 1 }).Routes[0];
        var beam = ComboSearch.Find(_g, new ComboOptions { Strategy = ComboStrategy.Beam, BeamWidth = 50, Top = 1 }).Routes[0];
        Assert.Equal(dfs.DamageKnown, best.DamageKnown);
        Assert.Equal(dfs.DamageKnown, beam.DamageKnown);
    }

    [Fact]
    public void ExpansionCapTruncatesHonestly()
    {
        var r = ComboSearch.Find(_g, new ComboOptions { MaxExpansions = 5 });
        Assert.True(r.Truncated);
        Assert.Contains(r.Warnings, w => w.Contains("stopped after"));
    }

    // ------------------------------------------------------------------ other fixtures / ownership

    [Fact]
    public void InferredOwnershipDowngradesEdgesAndIsListedAsUnmodelled()
    {
        // Park a shared block inside another Statedef, as real packages do (see state.ambiguous-owner).
        var path = Path.Combine(_fx.Root, "chars", "ComboGuy", "ComboGuy.cns");
        File.AppendAllText(path, "\n[State 0, parked]\ntype = ChangeState\nvalue = 220\ntrigger1 = command = \"z\"\ntrigger1 = movehit\n");
        var idx = CharacterSemanticIndexer.Build(_fx.Root, _fx.ComboGuy);
        var g = CandidateGraph.Build(idx);
        var parked = g.Edges.Single(e => e.From == "state:400" && e.To == "state:220" && e.ControllerId.StartsWith("state:400/"));
        Assert.Equal(Confidence.Inferred, parked.Confidence);
        Assert.Contains(parked.Unmodelled, u => u.Contains("owning state"));
        Assert.Contains(parked.Notes, n => n.Contains("declares State 0"));
    }

    [Fact]
    public void ValentineAndIkemenFixturesBuildGraphsWithoutFailing()
    {
        foreach (var entry in new[] { _fx.Valentine, _fx.Ikemen })
        {
            var g = CandidateGraph.Build(CharacterSemanticIndexer.Build(_fx.Root, entry));
            var report = g.Readiness();
            Assert.True(report.Edges > 0, entry.Id);
            Assert.All(g.Edges, e => Assert.NotEmpty(e.Evidence));
            var routes = ComboSearch.Find(g, new ComboOptions { Top = 5 });
            Assert.NotNull(routes);
        }
    }
}

public class XRayComboCliTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    public void Dispose() => _fx.Dispose();

    private string Target => Path.Combine(_fx.Root, "chars", "ComboGuy");

    private (int Code, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = IKEMENLab.Cli.CliApp.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void CombosCommandReturnsDeterministicCandidateRoutes()
    {
        var a = Cli("xray", "combos", Target, "--top", "5", "--meter", "1000");
        var b = Cli("xray", "combos", Target, "--top", "5", "--meter", "1000");
        Assert.Equal(0, a.Code);
        Assert.Equal(a.Out, b.Out);

        using var doc = System.Text.Json.JsonDocument.Parse(a.Out);
        var root = doc.RootElement;
        Assert.Equal("ikemenlab.xray.combo/1", root.GetProperty("schema").GetString());
        Assert.Equal("ikemenlab.xray/1", root.GetProperty("indexSchema").GetString());
        var routes = root.GetProperty("routes");
        Assert.Equal(5, routes.GetArrayLength());
        foreach (var r in routes.EnumerateArray())
        {
            Assert.Equal("candidate", r.GetProperty("status").GetString());
            Assert.NotEqual("RuntimeVerified", r.GetProperty("confidence").GetString());
            Assert.True(r.GetProperty("steps").GetArrayLength() > 0);
            foreach (var s in r.GetProperty("steps").EnumerateArray())
            {
                Assert.True(s.TryGetProperty("edge", out _));
                Assert.True(s.TryGetProperty("confidence", out _));
                Assert.True(s.TryGetProperty("unmodelled", out _));
            }
        }

        Assert.True(root.GetProperty("warnings").GetArrayLength() >= 1);
        Assert.DoesNotContain("RuntimeVerified", a.Out);
    }

    [Fact]
    public void CandidatesAndReadinessCommands()
    {
        using var all = System.Text.Json.JsonDocument.Parse(Cli("xray", "candidates", Target).Out);
        Assert.True(all.RootElement.GetProperty("edges").GetArrayLength() > 10);

        using var from = System.Text.Json.JsonDocument.Parse(Cli("xray", "candidates", Target, "--from", "1000").Out);
        var edges = from.RootElement.GetProperty("edges").EnumerateArray().ToList();
        Assert.All(edges, e => Assert.Equal("state:1000", e.GetProperty("from").GetString()));
        Assert.Contains(edges, e => e.GetProperty("to").GetString() == "state:3000" && e.GetProperty("facets").GetProperty("power")[0].GetProperty("value").GetDouble() == 1000);
        Assert.All(edges, e => Assert.NotEmpty(e.GetProperty("evidence").EnumerateArray()));

        using var ready = System.Text.Json.JsonDocument.Parse(Cli("xray", "readiness", Target).Out);
        Assert.Equal(1, ready.RootElement.GetProperty("dynamicTargets").GetInt32());
        Assert.True(ready.RootElement.GetProperty("findings").GetArrayLength() >= 2);

        Assert.Equal(3, Cli("xray", "candidates", Target, "--from", "9999").Code);
        Assert.Equal(2, Cli("xray", "combos", Target, "--max-moves", "many").Code);
    }

    [Fact]
    public void FlagsReachTheSearch()
    {
        using var strict = System.Text.Json.JsonDocument.Parse(Cli("xray", "combos", Target, "--top", "500").Out);
        using var loose = System.Text.Json.JsonDocument.Parse(Cli("xray", "combos", Target, "--top", "500", "--all-cancels", "--repeats", "2", "--strategy", "best").Out);
        Assert.False(strict.RootElement.GetProperty("options").GetProperty("allowLinks").GetBoolean());
        Assert.False(loose.RootElement.GetProperty("options").GetProperty("hitConfirmOnly").GetBoolean());
        Assert.Equal("BestFirst", loose.RootElement.GetProperty("options").GetProperty("strategy").GetString());
        Assert.True(loose.RootElement.GetProperty("routes").GetArrayLength() > strict.RootElement.GetProperty("routes").GetArrayLength());
    }
}
