using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Player playback eligibility. The Aizen-style character is SYNTHETIC: reconstructed from a description of a diagnostic whose routes
/// used AILevel-gated branches; it is not the real character and no real diagnostic file was available.
/// </summary>
public class XRayEligibilityTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly CandidateGraph _g;
    private readonly IReadOnlyList<ComboRoute> _routes;

    public XRayEligibilityTests()
    {
        _g = CandidateGraph.Build(CharacterSemanticIndexer.Build(_fx.Root, _fx.AizenStyle));
        _routes = ComboSearch.Find(_g, new ComboOptions { MaxMoves = 3, Top = 2000, HitConfirmOnly = false }).Routes;
    }

    public void Dispose() => _fx.Dispose();

    private IEnumerable<(ComboRoute Route, RouteEligibility Eligibility)> Classified() => _routes.Select(r => (r, PlaybackEligibility.Classify(_g, r)));

    private ComboRoute Pick(string states, Func<CandidateEdge, bool>[] perStep)
    {
        var want = states.Split('>');
        return _routes.First(r => r.States.Select(s => s.Replace("state:", "")).SequenceEqual(want) &&
                                  r.Steps.Select((s, i) => perStep[i](s.Edge)).All(x => x));
    }

    private bool ReadsAi(CandidateEdge e) => e.Facets.ReadsAiLevel;

    [Fact]
    public void AiOnlyBranchesAreProvenNotPlayerScriptableWithTheExactReason()
    {
        var route = Pick("200>210>1000", [ReadsAi, e => !ReadsAi(e), ReadsAi]);
        var el = PlaybackEligibility.Classify(_g, route);
        Assert.Equal(PlayerEligibility.NotPlayerScriptable, el.Eligibility);
        Assert.Equal(
            "Cannot script this candidate: Step 1 requires AILevel > 0 in the selected branch; player playback uses AILevel = 0. Step 3 also selects an AI-only branch.",
            el.Reason);
    }

    [Fact]
    public void AiComparisonOperatorAndValueAreEvaluatedAgainstTheContext()
    {
        var route = Pick("200>210>1000", [e => !ReadsAi(e), e => !ReadsAi(e), ReadsAi]);   // step 3 is AILevel >= 1
        Assert.Contains("Step 3 requires AILevel >= 1", PlaybackEligibility.Classify(_g, route).Reason);
        // The same branch is compatible with a context that runs at AILevel 1: classification follows the context, not a constant.
        Assert.NotEqual(PlayerEligibility.NotPlayerScriptable, PlaybackEligibility.Classify(_g, route, new PlaybackContext(1)).Eligibility);
    }

    [Fact]
    public void ThePlayerOnlyRouteIsScriptableAndItsStaticConfidenceIsUntouched()
    {
        var route = Pick("200>210>1000", [e => !ReadsAi(e), e => !ReadsAi(e), e => !ReadsAi(e)]);
        var el = PlaybackEligibility.Classify(_g, route);
        // Eligibility says nothing about static confidence: the route keeps the one it was found with.
        Assert.Equal(Confidence.Inferred, route.Confidence);
        Assert.NotEqual(PlayerEligibility.NotPlayerScriptable, el.Eligibility);
        Assert.Null(el.Reason);
    }

    [Fact]
    public void ABranchWithNoInjectableCommandIsProvenNotScriptable()
    {
        var route = _routes.First(r => r.States.Select(s => s.Replace("state:", "")).SequenceEqual(["200", "230"]));
        var el = PlaybackEligibility.Classify(_g, route);
        Assert.Equal(PlayerEligibility.NotPlayerScriptable, el.Eligibility);
        Assert.Contains("Step 2 selects a branch with no injectable command", el.Reason);
        Assert.Contains(el.Steps[1].Findings, f => f.Code == EligibilityCodes.NoPlayerEntry && f.Proven);
    }

    [Fact]
    public void AnUnmodelledConditionIsUnknownNeverRejected()
    {
        var route = _routes.First(r => r.States.Select(s => s.Replace("state:", "")).SequenceEqual(["220"]));
        var el = PlaybackEligibility.Classify(_g, route);
        Assert.Equal(PlayerEligibility.EligibilityUnknown, el.Eligibility);
        Assert.Null(el.Reason);
        Assert.Contains("not modelled", el.UnknownNote);
    }

    [Fact]
    public void AiLevelInsideAnOrIsUnknownBecauseItIsNotAComparisonTheAnalyzerModels()
    {
        var route = _routes.First(r => r.States.Select(s => s.Replace("state:", "")).SequenceEqual(["240"]));
        var el = PlaybackEligibility.Classify(_g, route);
        Assert.Equal(PlayerEligibility.EligibilityUnknown, el.Eligibility);
        Assert.Contains(el.Steps[0].Findings, f => f.Code == EligibilityCodes.AiLevelUnmodelled && !f.Proven);
    }

    [Fact]
    public void AnAiLevelZeroBranchIsCompatibleWithPlayerPlayback()
    {
        var route = _routes.First(r => r.States.Select(s => s.Replace("state:", "")).SequenceEqual(["250"]));
        Assert.DoesNotContain(PlaybackEligibility.Classify(_g, route).Steps[0].Findings, f => f.Proven);
    }

    [Fact]
    public void ChangingStateIsNotTreatedAsAFixedContext()
    {
        // StateType / ctrl / movehit / stateno conditions never produce a finding on their own.
        var route = Pick("200>210>1000", [e => !ReadsAi(e), e => !ReadsAi(e), e => !ReadsAi(e)]);
        Assert.All(PlaybackEligibility.Classify(_g, route).Steps, s => Assert.DoesNotContain(s.Findings, f => f.Proven));
    }

    [Fact]
    public void EveryRouteGetsExactlyOneClassAndAiRoutesAreSeparable()
    {
        var all = Classified().ToList();
        Assert.Contains(all, x => x.Eligibility.Eligibility == PlayerEligibility.NotPlayerScriptable);
        Assert.Contains(all, x => x.Eligibility.Eligibility != PlayerEligibility.NotPlayerScriptable);
        Assert.All(all.Where(x => x.Eligibility.IsNotPlayerScriptable), x => Assert.StartsWith("Cannot script this candidate: Step ", x.Eligibility.Reason));
    }
}
