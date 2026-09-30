using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;
using Xunit;

namespace IKEMENLab.Tests;

public class XRayIndexTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly SemanticIndex _v;
    private readonly SemanticIndex _i;

    public XRayIndexTests()
    {
        _v = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine);
        _i = CharacterSemanticIndexer.Build(_fx.Root, _fx.Ikemen);
    }

    public void Dispose() => _fx.Dispose();

    private static Relationship Rel(SemanticIndex idx, RelationKind kind, string from, string to) =>
        idx.Relationships.Single(r => r.Kind == kind && r.From == from && r.To == to);

    // ------------------------------------------------------------------ provenance and IDs

    [Fact]
    public void ProvenancePointsAtTheRealLineInTheRealFile()
    {
        var ctrl = _v.Get("state:200/ctrl:0")!;
        var file = _v.FileOf(ctrl.Source)!;
        Assert.Equal("chars/Valentine/Valentine.cns", file.RelPath);
        var lines = SourceLexer.SplitLines(File.ReadAllText(Path.Combine(_fx.Root, file.RelPath.Replace('/', Path.DirectorySeparatorChar)))).ToList();
        Assert.Equal("[State 200, hit]", lines[ctrl.Source!.Value.StartLine - 1].Trim());
        Assert.True(ctrl.Source.Value.EndLine > ctrl.Source.Value.StartLine);
    }

    [Fact]
    public void EveryStructuralObjectHasProvenanceInsideItsFile()
    {
        foreach (var o in _v.Objects.Where(o => o.Kind is ObjectKind.State or ObjectKind.Controller or ObjectKind.Command or ObjectKind.Animation or ObjectKind.AnimFrame or ObjectKind.Clsn or ObjectKind.HitDef && !o.IsStub))
        {
            Assert.True(o.Source is not null, o.Id);
            Assert.InRange(o.Source!.Value.FileId, 0, _v.Files.Count - 1);
            Assert.True(o.Source.Value.StartLine >= 1 && o.Source.Value.EndLine >= o.Source.Value.StartLine, o.Id);
        }
    }

    [Fact]
    public void IdsAreStableAcrossRebuildsAndWhenLinesShift()
    {
        var again = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine);
        Assert.Equal(_v.Objects.Select(o => o.Id), again.Objects.Select(o => o.Id));
        Assert.Equal(_v.Relationships.Select(r => r.Id), again.Relationships.Select(r => r.Id));

        var cns = Path.Combine(_fx.Root, "chars", "Valentine", "Valentine.cns");
        File.WriteAllText(cns, "; padding\n; padding\n; padding\n\n\n" + File.ReadAllText(cns));
        var shifted = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine);
        Assert.Equal(_v.Objects.Select(o => o.Id), shifted.Objects.Select(o => o.Id));
        Assert.Equal(_v.Get("state:200/ctrl:0")!.Source!.Value.StartLine + 5, shifted.Get("state:200/ctrl:0")!.Source!.Value.StartLine);
    }

    // ------------------------------------------------------------------ commands and gates

    [Fact]
    public void CommandsKeepTheirInputDefinitionAndAlternates()
    {
        var d4c = _v.Get("cmd:D4C")!;
        Assert.Equal("20", d4c.Prop("time"));
        Assert.Equal("1", d4c.Prop("buffer"));
        Assert.Equal("~D , DF , F , x+y", d4c.Prop("steps"));
        Assert.Equal("3", d4c.Prop("directionSteps"));
        Assert.Equal("1", d4c.Prop("buttonSteps"));
        Assert.Equal("cmd:x", _v.Get("cmd:x#2")!.Prop("alternateOf"));
        Assert.Equal(string.Empty, _v.Get("cmd:broken")!.Prop("raw")); // kept, empty, and reported below
        Assert.Contains(_v.Diagnostics, d => d.Code == "command.no-input");
    }

    [Fact]
    public void GateFacetsCaptureCommandPowerCtrlAndStateType()
    {
        var gate = _v.Get("state:-1/ctrl:0")!.Gate!;
        Assert.Equal(1, gate.TriggerAll.Count(l => l.Text.StartsWith("command")));
        var first = gate.Branches.First(b => b.Number == 1).Facets;
        Assert.Equal(["D4C"], first.Commands);
        Assert.Equal([">="], first.Power.Select(p => p.Op));
        Assert.Equal(1000, first.Power[0].Value);
        Assert.True(first.CtrlRequired);
        Assert.Equal(["s"], first.StateTypes);
        Assert.Equal(0, first.OtherConditions);

        var second = gate.Branches.First(b => b.Number == 2).Facets;
        Assert.Contains("movecontact", second.Contact);
        Assert.Equal(1, second.OtherConditions); // stateno = [200,210] is not a modelled shape, and says so
    }

    [Fact]
    public void OrOfCommandsExpandsIntoSeparateAlternatives()
    {
        var facets = _v.Get("state:-1/ctrl:1")!.Gate!.Branches.Select(b => b.Facets).ToList();
        Assert.Equal(2, facets.Count);
        Assert.Contains(facets, f => f.Commands.SequenceEqual(["LoveTrain"]));
        Assert.Contains(facets, f => f.Commands.SequenceEqual(["D4C_alt"]));
    }

    [Fact]
    public void UndefinedCommandIsAnUnknownReference()
    {
        var r = Rel(_v, RelationKind.ReferencesCommand, "state:-1/ctrl:1", "cmd:D4C_alt");
        Assert.Equal(Confidence.Unknown, r.Confidence);
        Assert.True(_v.Get("cmd:D4C_alt")!.IsStub);
        Assert.Equal(Confidence.StaticProven, Rel(_v, RelationKind.ReferencesCommand, "state:-1/ctrl:0", "cmd:D4C").Confidence);
    }

    // ------------------------------------------------------------------ state graph

    [Fact]
    public void LiteralChangeStateIsStaticProvenWithItsGate()
    {
        var r = Rel(_v, RelationKind.ChangesState, "state:-1/ctrl:0", "state:3000");
        Assert.Equal(Confidence.StaticProven, r.Confidence);
        Assert.Equal("state.literal-change", r.Evidence[0].RuleId);
        Assert.NotNull(r.Gate);
        Assert.Same(_v.Get("state:-1/ctrl:0")!.Gate, r.Gate);
    }

    [Fact]
    public void DynamicAndUnparseableTargetsAreUnknownNotGuessed()
    {
        var dyn = _v.Outgoing("state:-1/ctrl:4", RelationKind.ChangesState).Single();
        Assert.Equal(Confidence.Unknown, dyn.Confidence);
        Assert.Equal("state.dynamic-target", dyn.Evidence[0].RuleId);
        Assert.StartsWith("dynamic:", dyn.To);

        var broken = _v.Outgoing("state:10890/ctrl:1", RelationKind.ChangesState).Single();
        Assert.Equal("expr.unparsed", broken.Evidence[0].RuleId);
        Assert.Equal("true", _v.Get("state:10890/ctrl:1")!.Prop("unparsed"));
    }

    [Fact]
    public void ConstantArithmeticTargetsAreFolded()
    {
        var path = Path.Combine(_fx.Root, "chars", "IkemenGuy", "IkemenGuy.cns");
        File.AppendAllText(path, "\n[State 8000, fold]\ntype = ChangeState\nvalue = 100 + 100\ntrigger1 = 1\n");
        var idx = CharacterSemanticIndexer.Build(_fx.Root, _fx.Ikemen);
        var r = idx.Outgoing("state:8000/ctrl:1", RelationKind.ChangesState).Single();
        Assert.Equal("state:200", r.To);
        Assert.Equal(Confidence.StaticProven, r.Confidence);
    }

    [Fact]
    public void CommonStatesResolveWhenLoadedAndAreInferredWhenNot()
    {
        Assert.Equal(Confidence.StaticProven, Rel(_v, RelationKind.ChangesState, "state:200/ctrl:2", "state:0").Confidence);
        Assert.Equal("true", _v.Get("state:0")!.Prop("common"));

        var without = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine, new XRayOptions { IncludeCommonStates = false });
        var r = Rel(without, RelationKind.ChangesState, "state:200/ctrl:2", "state:0");
        Assert.Equal(Confidence.Inferred, r.Confidence);
        Assert.Equal("state.engine-common", r.Evidence[0].RuleId);
        Assert.NotEqual(Confidence.StaticProven, r.Confidence);
    }

    [Fact]
    public void MissingStateIsUnknown()
    {
        var path = Path.Combine(_fx.Root, "chars", "IkemenGuy", "IkemenGuy.cns");
        File.AppendAllText(path, "\n[State 8000, nowhere]\ntype = ChangeState\nvalue = 77777\ntrigger1 = 1\n");
        var idx = CharacterSemanticIndexer.Build(_fx.Root, _fx.Ikemen);
        var r = Rel(idx, RelationKind.ChangesState, "state:8000/ctrl:1", "state:77777");
        Assert.Equal(Confidence.Unknown, r.Confidence);
        Assert.Equal("state.missing", r.Evidence[0].RuleId);
    }

    [Fact]
    public void DuplicateStatedefKeepsBothAndMarksTheShadowedOne()
    {
        Assert.Equal("S", _v.Get("state:200")!.Prop("p.type"));
        var shadow = _v.Get("state:200#2")!;
        Assert.Equal("true", shadow.Prop("shadowed"));
        Assert.Equal("C", shadow.Prop("p.type"));
        Assert.Contains(_v.Diagnostics, d => d.Code == "state.duplicate");
        Assert.Null(_v.Get("state:abc"));
        Assert.Contains(_v.Diagnostics, d => d.Code == "state.bad-number");
    }

    [Fact]
    public void EntryPointsIncludeEngineStatesAndAiGates()
    {
        Assert.Equal(Confidence.StaticProven, Rel(_v, RelationKind.EntryPoint, "char:Valentine", "state:-1").Confidence);
        var ai = _v.Incoming("state:-1/ctrl:5", RelationKind.EntryPoint).Single();
        Assert.Equal("entry.ai-trigger", ai.Evidence[0].RuleId);
        Assert.Equal("ai", ai.Prop("kind"));
        Assert.Equal(["state:-1/ctrl:4", "state:-1/ctrl:5"], _v.Outgoing("char:Valentine", RelationKind.EntryPoint).Where(r => r.Prop("kind") == "ai").Select(r => r.To).OrderBy(x => x).ToList());
        Assert.Contains(_i.Outgoing("char:IkemenGuy", RelationKind.EntryPoint), r => r.To == "state:-2");
        Assert.Contains(_i.Outgoing("char:IkemenGuy", RelationKind.EntryPoint), r => r.To == "state:-3");
    }

    // ------------------------------------------------------------------ victim states / throws

    [Fact]
    public void VictimStatesAndBindsAreRecordedLiterally()
    {
        var r = Rel(_v, RelationKind.SetsVictimState, "state:210/ctrl:0/hitdef", "state:1590");
        Assert.Equal(Confidence.StaticProven, r.Confidence);
        Assert.Equal(Confidence.StaticProven, Rel(_v, RelationKind.Binds, "state:210/ctrl:1", "entity:target").Confidence);
        Assert.Equal("1", _v.Get("state:210/ctrl:0/hitdef")!.Prop("p.p2getp1state"));
        Assert.Equal("S, NT", _v.Get("state:210/ctrl:0/hitdef")!.Prop("p.attr"));
    }

    [Fact]
    public void ThrowNeedsABind_AndTheLabelIsInferred()
    {
        var throwAbility = _v.Get("ability:1500")!;
        var label = Assert.Single(throwAbility.Labels, l => l.Category == LabelCategories.AbilityCategory && l.Text == "Throw");
        Assert.Equal(Confidence.Inferred, label.Confidence);

        // Love Train's victim state has no TargetBind, so it is not called a throw.
        var love = _v.Get("ability:10800")!;
        Assert.DoesNotContain(love.Labels, l => l.Text == "Throw");
        Assert.Contains(love.Labels, l => l.Text == "Victim-state hit");
        Assert.Equal("Super", love.Prop("category"));
    }

    // ------------------------------------------------------------------ helpers and projectiles

    [Fact]
    public void HelperFamilyIsLinked()
    {
        Assert.Equal(Confidence.StaticProven, Rel(_v, RelationKind.SpawnsHelper, "state:3000/ctrl:0", "helper:340").Confidence);
        Assert.Equal(Confidence.StaticProven, Rel(_v, RelationKind.HelperRunsState, "helper:340", "state:340").Confidence);
        Assert.Equal("340", _v.Get("helper:340")!.Prop("statenos"));
        Assert.Equal("detector", _v.Get("helper:340")!.Prop("name"));
        var role = Assert.Single(_v.Get("helper:340")!.Labels, l => l.Category == LabelCategories.HelperRole);
        Assert.Equal(Confidence.Inferred, role.Confidence);
        Assert.Equal("helper.follower", role.RuleId);
    }

    [Fact]
    public void ProjectileLinksItsAnimationAndVictimState()
    {
        Assert.Equal(Confidence.StaticProven, Rel(_v, RelationKind.SpawnsProjectile, "state:10800/ctrl:1", "proj:10801").Confidence);
        // The fixture's AIR never defines action 10801, so the projectile animation is a missing reference, not a guess.
        var anim = Rel(_v, RelationKind.UsesAnim, "proj:10801", "anim:10801");
        Assert.Equal(Confidence.Unknown, anim.Confidence);
        Assert.Equal("anim.missing", anim.Evidence[0].RuleId);
        Assert.Equal(Confidence.StaticProven, Rel(_v, RelationKind.SetsVictimState, "state:10800/ctrl:1/hitdef", "state:10890").Confidence);
    }

    // ------------------------------------------------------------------ variables

    [Fact]
    public void VariableWritesReadsAndResetsAreTrackedWithScope()
    {
        var writes12 = _v.Incoming("var:var:12", RelationKind.WritesVar);
        Assert.Contains(writes12, r => r.From == "state:3000/ctrl:1" && r.Prop("value") == "1");
        Assert.Contains(writes12, r => r.From == "state:10800/ctrl:2" && r.Prop("value") == "0");
        Assert.Contains(_v.Incoming("var:var:12", RelationKind.ResetsVar), r => r.From == "state:10800/ctrl:2");

        var read = _v.Incoming("var:var:12", RelationKind.ReadsVar).Single(r => r.From == "state:340/ctrl:0");
        Assert.Equal("root", read.Prop("scope"));

        var helperRead = _v.Incoming("var:var:3", RelationKind.ReadsVar).Single();
        Assert.Equal("helper", helperRead.Prop("scope"));
        Assert.Equal("340", helperRead.Prop("scopeArg"));

        Assert.Equal("1", _v.Incoming("var:var:20", RelationKind.WritesVar).Single().Prop("step"));
        Assert.Contains(_v.Incoming("var:var:5", RelationKind.ReadsVar), r => r.From == "state:-1/ctrl:4");
    }

    [Fact]
    public void VariableNamesAreInferredLabelsNeverFacts()
    {
        foreach (var v in _v.Of(ObjectKind.Variable))
            foreach (var l in v.Labels)
                Assert.Equal(Confidence.Inferred, l.Confidence);
        Assert.Contains(_v.Get("var:var:12")!.Labels, l => l.RuleId == "var.boolean-flag");
        Assert.Contains(_v.Get("var:var:3")!.Labels, l => l.RuleId == "var.ai-read");
    }

    [Fact]
    public void DynamicVariableIndexIsUnknownAndUsesAWildcardObject()
    {
        var path = Path.Combine(_fx.Root, "chars", "IkemenGuy", "IkemenGuy.cns");
        File.AppendAllText(path, "\n[State 8000, dyn]\ntype = Null\ntrigger1 = var(var(1)) > 0\n");
        var idx = CharacterSemanticIndexer.Build(_fx.Root, _fx.Ikemen);
        var r = idx.Incoming("var:var:*", RelationKind.ReadsVar).Single();
        Assert.Equal(Confidence.Unknown, r.Confidence);
    }

    // ------------------------------------------------------------------ power

    [Fact]
    public void PowerGatesAndCostsAreLiteral()
    {
        var gate = Rel(_v, RelationKind.GatedByPower, "state:-1/ctrl:0", "resource:power");
        Assert.Equal((">=", "1000"), (gate.Prop("op"), gate.Prop("value")));
        var cost = Rel(_v, RelationKind.ResourceCost, "state:3000", "resource:power");
        Assert.Equal("-1000", cost.Prop("amount"));
        Assert.Equal("30", Rel(_v, RelationKind.ResourceCost, "state:200", "resource:power").Prop("amount"));
    }

    // ------------------------------------------------------------------ animation and CLSN

    [Fact]
    public void AnimationFramesTicksAndBoxesFollowDefaultInheritance()
    {
        var a = _v.Get("anim:200")!;
        Assert.Equal("4", a.Prop("frames"));
        Assert.Equal("16", a.Prop("totalTicks"));
        Assert.Equal("6", _v.Get("anim:200/frame:2")!.Prop("startTick"));

        // frame 0: default hurtbox only; frame 1: local attack box + inherited hurtbox; frame 2: default again.
        Assert.NotNull(_v.Get("anim:200/frame:0/clsn2:0"));
        Assert.Null(_v.Get("anim:200/frame:0/clsn1:0"));
        Assert.Equal("10", _v.Get("anim:200/frame:1/clsn1:0")!.Prop("x1"));
        Assert.Equal("false", _v.Get("anim:200/frame:1/clsn1:0")!.Prop("inherited"));
        Assert.Equal("true", _v.Get("anim:200/frame:1/clsn2:0")!.Prop("inherited"));
        Assert.Null(_v.Get("anim:200/frame:2/clsn1:0"));
        Assert.NotNull(_v.Get("anim:200/frame:2/clsn2:0"));
    }

    [Fact]
    public void LoopStartInfiniteTicksAndBadFramesAreHandled()
    {
        var a = _v.Get("anim:210")!;
        Assert.Equal("1", a.Prop("loopStart"));
        Assert.Equal("true", a.Prop("endsInfinite"));
        Assert.Contains(_v.Diagnostics, d => d.Code == "air.bad-frame");
    }

    [Fact]
    public void SpritesResolveAgainstTheSffAndMissingOnesAreUnknown()
    {
        var ok = Rel(_v, RelationKind.AnimUsesSprite, "anim:200/frame:0", "sprite:200,0");
        Assert.Equal(Confidence.StaticProven, ok.Confidence);
        Assert.Equal("4", _v.Get("sprite:200,0")!.Prop("width"));
        Assert.Equal("10", _v.Get("sprite:200,0")!.Prop("axisX"));

        var missing = Rel(_v, RelationKind.AnimUsesSprite, "anim:200/frame:3", "sprite:200,999");
        Assert.Equal(Confidence.Unknown, missing.Confidence);
        Assert.True(_v.Get("sprite:200,999")!.IsStub);
    }

    [Fact]
    public void StateAnimationLinksAndAnimElemGatesMapToFrames()
    {
        Assert.Equal(Confidence.StaticProven, Rel(_v, RelationKind.UsesAnim, "state:200", "anim:200").Confidence);
        var timeline = Rel(_v, RelationKind.ActiveAtFrame, "state:200/ctrl:0", "anim:200/frame:2");
        Assert.Equal("timeline.animelem-literal", timeline.Evidence[0].RuleId);
        Assert.Equal(Confidence.StaticProven, timeline.Confidence);
        Assert.Equal("AnimElem = 3", timeline.Prop("gate"));

        // A time-based gate maps to a frame only by inference.
        var time = _v.Outgoing("state:10800/ctrl:2", RelationKind.ActiveAtFrame);
        Assert.All(time, r => Assert.Equal(Confidence.Inferred, r.Confidence));
    }

    [Fact]
    public void DynamicAnimationIsUnknown()
    {
        var r = _i.Outgoing("state:200", RelationKind.UsesAnim).Single();
        Assert.Equal(Confidence.Unknown, r.Confidence);
        Assert.Equal("anim.dynamic", r.Evidence[0].RuleId);
        Assert.Empty(_i.Outgoing("state:200/ctrl:0", RelationKind.ActiveAtFrame));
    }

    [Fact]
    public void IkemenStyleFixture()
    {
        Assert.Equal(Confidence.StaticProven, Rel(_i, RelationKind.ActiveAtFrame, "state:1000/ctrl:0", "anim:1000/frame:2").Confidence);
        Assert.Equal(Confidence.Unknown, Rel(_i, RelationKind.UsesAnim, "proj:1010", "anim:1010").Confidence);
        Assert.Equal("3", _i.Get("anim:200")!.Prop("frames"));
        // Clsn1: 0 declares "no attack boxes" for that frame only; hurtboxes still come from Clsn2Default.
        Assert.NotNull(_i.Get("anim:200/frame:2/clsn2:1"));
        Assert.Null(_i.Get("anim:200/frame:2/clsn1:0"));
        Assert.Contains(_i.Diagnostics, d => d.Code == "zss.not-indexed");
        var gate = _i.Get("state:-1/ctrl:0")!.Gate!.Branches[0].Facets;
        Assert.Equal(["QCF_x"], gate.Commands);
        Assert.Equal(["a!"], gate.StateTypes);
        Assert.True(gate.CtrlRequired);
        Assert.Equal(1, gate.OtherConditions); // Time = [0,20]
        Assert.Equal(1, _i.Get("state:200/ctrl:0")!.Gate!.Branches[0].Facets.AnimElem.Count);
    }

    // ------------------------------------------------------------------ abilities

    [Fact]
    public void AbilitiesAreGroupedFromCommandEntries()
    {
        var ids = _v.Of(ObjectKind.Ability).Select(a => a.Id).ToList();
        Assert.Contains("ability:3000", ids);
        Assert.Contains("ability:10800", ids);
        Assert.Contains("ability:1500", ids);
        Assert.Contains("ability:100", ids);

        var love = _v.Get("ability:10800")!;
        Assert.Equal("command", love.Prop("entry"));
        var members = _v.Incoming("ability:10800", RelationKind.PartOf).Select(r => r.From).ToHashSet();
        Assert.Contains("state:10800", members);
        Assert.Contains("state:10890", members);          // victim path
        Assert.Contains("proj:10801", members);
        Assert.Contains("var:var:12", members);
        Assert.Contains("cmd:LoveTrain", members);
        Assert.DoesNotContain("state:0", members);        // common state: exit, not member
    }

    [Fact]
    public void AbilityMembershipConfidenceFollowsTheEdges()
    {
        var static10800 = _v.Incoming("ability:10800", RelationKind.PartOf).Single(r => r.From == "state:10800");
        Assert.Equal(Confidence.StaticProven, static10800.Confidence);
        Assert.All(_v.Get("ability:10800")!.Labels, l => Assert.Equal(Confidence.Inferred, l.Confidence));
        Assert.Equal("Mobility", _v.Get("ability:100")!.Prop("category"));
        Assert.Equal("Summon", _v.Get("ability:3000")!.Labels.Single(l => l.Text == "Summon").Text);
    }

    [Fact]
    public void AiOnlyEntriesBecomeAbilitiesTooAndAreMarked()
    {
        Assert.Equal("ai", _v.Get("ability:1500")!.Prop("entry"));
        Assert.Null(_v.Get("ability:1500")!.Prop("commands"));
    }

    // ------------------------------------------------------------------ encodings and the evidence contract

    [Fact]
    public void ShiftJisCommentSurvivesAsTheAuthorName()
    {
        var label = _v.Get("state:3001")!.Labels.Single(l => l.Category == "name");
        Assert.StartsWith("必殺技", label.Text);
        Assert.Equal(Confidence.Inferred, label.Confidence);
    }

    [Fact]
    public void EvidenceContractHoldsForBothCharacters()
    {
        foreach (var idx in new[] { _v, _i })
        {
            Assert.All(idx.Relationships, r =>
            {
                Assert.NotEmpty(r.Evidence);
                Assert.All(r.Evidence, e => Assert.True(EvidenceRules.Exists(e.RuleId), e.RuleId));
                Assert.Equal(EvidenceRules.ConfidenceOf(r.Evidence[0].RuleId), r.Confidence);
                Assert.NotEqual(Confidence.RuntimeVerified, r.Confidence);
            });
            foreach (var o in idx.Objects)
                Assert.All(o.Labels, l =>
                {
                    Assert.Equal(EvidenceRules.ConfidenceOf(l.RuleId), l.Confidence);
                    Assert.NotEqual(Confidence.StaticProven, l.Confidence); // interpretations are never presented as literal facts
                    Assert.NotEqual(Confidence.RuntimeVerified, l.Confidence);
                });
        }
    }

    [Fact]
    public void EveryRuleIdIsDocumentedAndNoRuleClaimsRuntimeEvidence()
    {
        Assert.DoesNotContain(EvidenceRules.Rules, r => r.Confidence == Confidence.RuntimeVerified);
        Assert.Equal(EvidenceRules.Rules.Count, EvidenceRules.Rules.Select(r => r.Id).Distinct().Count());
        Assert.All(EvidenceRules.Rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public void CorruptedFilesNeverThrow()
    {
        var rng = new Random(2024);
        var names = new[] { "Valentine.cmd", "Valentine.cns", "Valentine.st", "Valentine.air", "Valentine.def" };
        var dir = Path.Combine(_fx.Root, "chars", "Valentine");
        var originals = names.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(dir, n)));
        for (var round = 0; round < 60; round++)
        {
            foreach (var n in names)
            {
                var bytes = originals[n].ToList();
                for (var k = rng.Next(1, 12); k > 0 && bytes.Count > 0; k--)
                {
                    var pos = rng.Next(bytes.Count);
                    switch (rng.Next(3))
                    {
                        case 0: bytes.RemoveAt(pos); break;
                        case 1: bytes.Insert(pos, (byte)rng.Next(256)); break;
                        default: bytes.RemoveRange(pos, Math.Min(rng.Next(1, 40), bytes.Count - pos)); break;
                    }
                }

                File.WriteAllBytes(Path.Combine(dir, n), bytes.ToArray());
            }

            var idx = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine);
            Assert.NotNull(idx);
            Assert.All(idx.Relationships, r => Assert.NotEmpty(r.Evidence));
        }
    }

    [Fact]
    public void MissingFilesBecomeDiagnostics()
    {
        File.Delete(Path.Combine(_fx.Root, "chars", "Valentine", "Valentine.air"));
        File.Delete(Path.Combine(_fx.Root, "chars", "Valentine", "Valentine.cmd"));
        var idx = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine);
        Assert.Contains(idx.Diagnostics, d => d.Code == "file.missing");
        Assert.NotNull(idx.Get("state:200"));
    }

    [Fact]
    public void UnreadableDefGivesAnErrorDiagnosticAndAnEmptyIndex()
    {
        var idx = CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine with { DefPath = "chars/Valentine/nope.def" });
        Assert.Contains(idx.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Single(idx.Objects);
    }
}
