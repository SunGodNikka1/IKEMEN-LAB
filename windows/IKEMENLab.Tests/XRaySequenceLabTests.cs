using System.Text.Json;
using System.Text.RegularExpressions;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Phase 3: Sequence Lab. Sequences become one plan for the existing driver; abilities are judged by the unchanged route verifier, movement by the
/// driver's bookkeeping checked against the samples, and the run is classified True Combo / Connected Sequence / Did Not Connect / Could Not Test.
/// </summary>
public class XRaySequenceLabTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly SemanticIndex _index;
    private readonly CandidateGraph _g;
    private readonly List<string> _cleanup = [];

    public XRaySequenceLabTests()
    {
        _index = CharacterSemanticIndexer.Build(_fx.Root, _fx.ComboGuy);
        _g = CandidateGraph.Build(_index);
    }

    public void Dispose()
    {
        _fx.Dispose();
        foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private string Temp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    /// <summary>The sequences the mock engine plays for the Phase 3 fixtures (scenario → spec).</summary>
    public static readonly IReadOnlyDictionary<string, string> MockSequences = new Dictionary<string, string>
    {
        ["seq_true"] = "200 > 210",
        ["seq_wait"] = "200 > wait:8 > 210",
        ["seq_gap"] = "200 > walk:5 > 220",
        ["seq_chase"] = "200 > chase:40 > 220",
        ["seq_far"] = "200 > back:10 > 220",
        ["seq_recover"] = "200 > chase:20! > 220",
        ["seq_jump"] = "200 > jump",
    };

    private Sequence Seq(string spec, int version = 1) => new("seq-test", "Test", version, SequenceSpec.Parse(spec));

    private SequencePlanResult Planned(string spec)
    {
        var r = SequencePlanner.Plan(_g, Seq(spec), 60);
        Assert.True(r.Plan is not null, r.Refused);
        return r;
    }

    /// <summary>Regenerates the plans for the mock engine: IKEMENLAB_DUMP_SEQ_DIR=dir dotnet test --filter DumpPhase3MockPlans.</summary>
    [Fact]
    public void DumpPhase3MockPlans()
    {
        if (Environment.GetEnvironmentVariable("IKEMENLAB_DUMP_SEQ_DIR") is not { Length: > 0 } dir) return;
        foreach (var (scenario, spec) in MockSequences) File.WriteAllText(Path.Combine(dir, scenario + ".lua"), InputPlanner.ToLua(Planned(spec).Plan!));
    }

    /// <summary>A trace the mock engine produced by running the real driver and probe on the scenario's plan, re-identified to the current plan.</summary>
    private static string FixtureText(string scenario, InputPlan plan) =>
        Regex.Replace(File.ReadAllText(Path.Combine(FixtureDir, $"xray_driver_mock_{scenario}.jsonl")), "\"planFingerprint\":\"[0-9A-F]*\"", "\"planFingerprint\":\"" + InputPlanner.Fingerprint(plan) + "\"");

    private (SequenceReport Report, InputPlan Plan) Run(string scenario)
    {
        var planned = Planned(MockSequences[scenario]);
        var path = Path.Combine(Temp("xray-seq-trace-"), "t.jsonl");
        File.WriteAllText(path, FixtureText(scenario, planned.Plan!));
        return (SequenceVerifier.Verify(planned.Plan!, TraceReader.ReadFile(path), planned.Steps.Select(s => s.Label).ToList()), planned.Plan!);
    }

    // ------------------------------------------------------------------ model and planner

    [Fact]
    public void TheSpecRoundTripsAndRejectsNonsense()
    {
        var actions = SequenceSpec.Parse("1000 > chase:35! > wait:8 > walk:12 > back:4 > dash > jump > ability:200");
        Assert.Equal("ability:1000 > chase:35! > wait:8 > walk:12 > back:4 > dash > jump > ability:200", SequenceSpec.Format(actions));
        Assert.True(actions[1].StopIfOpponentRecovers);
        Assert.Throws<FormatException>(() => SequenceSpec.Parse("fly:3"));
        Assert.Throws<FormatException>(() => SequenceSpec.Parse("wait:x"));
        Assert.Throws<FormatException>(() => SequenceSpec.Parse(" > "));
    }

    [Fact]
    public void AFollowUpIsARealCancelWhenTheGraphHasOneOtherwiseItsOwnCommandOnceYouCanAct()
    {
        var cancel = Planned("200 > 210");
        var step2 = cancel.Plan!.Steps[1];
        Assert.Equal(("Cancel", 200, 210, "hit"), (step2.Kind, step2.FromState!.Value, step2.ToState, step2.Contact));
        Assert.True(step2.Attack);
        Assert.StartsWith("Cancel State 200 into it by pressing “y” once it hits", cancel.Steps[1].How);

        var afterWalk = Planned("200 > walk:5 > 210");
        var follow = afterWalk.Plan!.Steps[2];
        Assert.Null(follow.FromState);                                              // walking ends the move: the follow-up is its own command
        Assert.Equal(("Start", "y"), (follow.Kind, follow.Command));
        Assert.Equal(StepAction.Hold, afterWalk.Plan.Steps[1].Action!.Kind);
        Assert.Equal(["F"], afterWalk.Plan.Steps[1].Action!.Keys);
        Assert.StartsWith("Once you can act, press “y”", afterWalk.Steps[2].How);

        var waited = Planned("200 > wait:8 > 210");                                 // a wait keeps the move: still the cancel, pressed later
        Assert.Equal(200, waited.Plan!.Steps[2].FromState);
        Assert.Equal((StepAction.Release, 8, false), (waited.Plan.Steps[1].Action!.Kind, waited.Plan.Steps[1].Action!.Frames!.Value, waited.Plan.Steps[1].Action!.WaitForControl));

        var chase = Planned("200 > chase:35! > 220").Plan!.Steps[1].Action!;
        Assert.Equal((StepAction.Chase, 35, true, SequencePlanner.ChaseGiveUpFrames), (chase.Kind, chase.Distance!.Value, chase.StopIfOpponentRecovers, chase.Frames!.Value));
    }

    [Fact]
    public void WhatCannotBePressedIsRefusedWithTheStepAndTheReason()
    {
        Assert.Equal((1, "A sequence starts with an ability (Play Ability), from neutral."), (SequencePlanner.Plan(_g, Seq("walk:5 > 200"), 60).RefusedStep, SequencePlanner.Plan(_g, Seq("walk:5 > 200"), 60).Refused));
        var super = SequencePlanner.Plan(_g, Seq("200 > walk:2 > 3000"), 60);      // 3000 is only a cancel out of 1000 on hit
        Assert.Equal(3, super.RefusedStep);
        Assert.Contains("cannot be started here", super.Refused);
        var dash = SequencePlanner.Plan(_g, Seq("200 > dash"), 60);
        Assert.Equal(2, dash.RefusedStep);
        Assert.Contains("no forward-dash command", dash.Refused);
        Assert.Contains("Frames must be", SequencePlanner.Plan(_g, Seq("200 > wait:0 > 210"), 60).Refused);
        Assert.Contains("only entered from other moves", SequencePlanner.Plan(_g, Seq("3000 > 200"), 60).Refused);
    }

    [Fact]
    public void SequenceFieldsNeverAppearInRouteOrAbilityPlansAndRoundTripInSequencePlans()
    {
        var route = ComboSearch.Find(_g, new ComboOptions { MaxMoves = 2, Top = 500 }).Routes.First(r => r.States.SequenceEqual(["state:200", "state:210"]));
        var routePlan = InputPlanner.Plan(_g, route).Plan!;
        Assert.DoesNotContain("\"action\"", InputPlanner.ToJson(routePlan));
        Assert.DoesNotContain("\"attack\"", InputPlanner.ToJson(routePlan));
        Assert.DoesNotContain("action =", InputPlanner.ToLua(routePlan));

        var plan = Planned("200 > chase:35! > wait:4 > 220").Plan!;
        var back = InputPlanner.FromJson(InputPlanner.ToJson(plan));
        Assert.Equal(InputPlanner.Fingerprint(plan), InputPlanner.Fingerprint(back));
        var action = Assert.IsType<StepAction>(back.Steps[1].Action);
        Assert.Equal(plan.Steps[1].Action, action with { Keys = plan.Steps[1].Action!.Keys });
        Assert.True(back.Steps[3].Attack);
        Assert.Contains("action = \"chase\"", InputPlanner.ToLua(plan));

        var judged = RouteVerifier.Verify(plan, new TraceLog { Events = [], Issues = [] });
        Assert.Equal((VerifyStatus.Inconclusive, VerifyReason.PlanMismatch), (judged.Status, judged.Reason));
    }

    // ------------------------------------------------------------------ verdicts on traces of the real driver (mock engine)

    [Fact]
    public void AbilityIntoAbilityIsATrueComboWhenTheOpponentNeverGetsOut()
    {
        var (r, _) = Run("seq_true");
        Assert.Equal(SequenceVerdict.TrueCombo, r.Verdict);
        Assert.All(r.Steps, s => Assert.Equal(SequenceStepOutcome.Done, s.Outcome));
        Assert.Equal((2, 2, 0, 0), (r.Attacks, r.ConnectedAttacks, r.OutOfHitstunFrames!.Value, r.OpponentCouldActFrames!.Value));
        Assert.All(r.Steps, s => Assert.Equal([AbilityVerifier.TransitionRule], s.RuntimeRules));
        Assert.Equal(100, r.Damage);
        Assert.StartsWith("True combo: all 2 attacks connected", r.Summary);
        Assert.True(r.Succeeded);
    }

    [Fact]
    public void AWaitBeforeTheFollowUpIsHonouredAndStillJudged()
    {
        var (r, plan) = Run("seq_wait");
        Assert.Equal(SequenceVerdict.TrueCombo, r.Verdict);
        var wait = r.Steps[1];
        Assert.Equal((SequenceStepOutcome.Done, "Wait 8f"), (wait.Outcome, wait.Label));
        Assert.Equal(8, (int)(wait.EndFrame!.Value - wait.StartFrame!.Value));
        Assert.True(r.Steps[2].InputFrame >= wait.EndFrame);                       // the follow-up was pressed after the wait
        Assert.Equal(StepAction.Release, plan.Steps[1].Action!.Kind);
    }

    [Fact]
    public void AFollowUpAfterTheOpponentRecoveredIsAConnectedSequenceNotACombo()
    {
        var (r, _) = Run("seq_gap");
        Assert.Equal(SequenceVerdict.ConnectedSequence, r.Verdict);
        Assert.Equal((2, 2), (r.Attacks, r.ConnectedAttacks));
        Assert.True(r.OutOfHitstunFrames > 0);
        Assert.True(r.OpponentCouldActFrames > 0);
        Assert.Contains("out of hitstun for", r.Summary);
        Assert.Contains("could act for", r.Summary);
        Assert.True(r.Succeeded);
    }

    [Fact]
    public void AChaseClosesTheGapBeforeTheFollowUp()
    {
        var (r, _) = Run("seq_chase");
        Assert.Equal(SequenceVerdict.ConnectedSequence, r.Verdict);                  // knocked away and recovered: a sequence, not a combo
        var chase = r.Steps[1];
        Assert.Equal((SequenceStepOutcome.Done, "Chase until within 40"), (chase.Outcome, chase.Label));
        Assert.True(r.Steps[2].DistanceAtStart <= 40);
        Assert.True(r.Steps[2].Connected);
    }

    [Fact]
    public void AFollowUpFromTooFarAwayDidNotConnectAndSaysHowFar()
    {
        var (r, _) = Run("seq_far");
        Assert.Equal((SequenceVerdict.DidNotConnect, SequenceReason.NoContact, 3), (r.Verdict, r.Reason, r.FailedStep));
        var whiff = r.Steps[2];
        Assert.Equal(SequenceStepOutcome.Done, whiff.Outcome);                      // the move did start
        Assert.False(whiff.Connected);
        Assert.Contains("did not touch the opponent: when it started they were standing", whiff.Why);
        Assert.Contains("away", whiff.Why);
        Assert.True(whiff.DistanceAtStart > 70);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void AnOpponentWhoRecoversDuringTheChaseStopsTheRun()
    {
        var (r, _) = Run("seq_recover");
        Assert.Equal((SequenceVerdict.DidNotConnect, SequenceReason.OpponentRecovered, 2), (r.Verdict, r.Reason, r.FailedStep));
        Assert.Equal(SequenceStepOutcome.Failed, r.Steps[1].Outcome);
        Assert.Contains("could act again", r.Steps[1].Why);
        Assert.Equal(SequenceStepOutcome.NotReached, r.Steps[2].Outcome);
        Assert.Contains("able to act", r.End!.OpponentPosture);
    }

    [Fact]
    public void AJumpIsSeenLeavingTheGroundAndOneAttackIsNotJudgedAsACombo()
    {
        var (r, _) = Run("seq_jump");
        Assert.Equal(SequenceStepOutcome.Done, r.Steps[1].Outcome);
        Assert.Equal(SequenceVerdict.ConnectedSequence, r.Verdict);
        Assert.Contains("no follow-up attack", r.Summary);
    }

    // ------------------------------------------------------------------ verdicts on hand-built traces

    private sealed class Trace
    {
        private long _f;
        private readonly List<TraceEvent> _events = [];
        public long Frame => _f;
        public Trace Driver(string kind, int? step = null, string? detail = null) { _events.Add(new DriverEvent(_f, null, kind, step, detail)); return this; }
        public Trace Input(int step, params string[] keys) { _events.Add(new InputEvent(_f, null, 1, keys, step, "input")); return this; }
        public Trace End(string reason) { _events.Add(new EndEvent(_f, null, reason)); return this; }

        public Trace Frames(int n, int p1, int p2 = 0, string p2Move = "I", double life = 1000, int hit = 0, double distance = 50, bool? p2Ctrl = null)
        {
            for (var i = 0; i < n; i++)
            {
                _f++;
                var a = new PlayerSample(p1, null, p1 == 0, "S", p1 == 0 ? "I" : "A", null, null, 1000, 0, 0, 0, null, null, 1, hit, hit, null);
                var b = new PlayerSample(p2, null, p2Ctrl ?? p2 == 0, "S", p2Move, null, null, life, 0, distance, 0, null, null, -1, 0, 0, null);
                _events.Add(new FrameEvent(_f, null, 1, a, b, distance, null, null, null, "derived:p2.x-p1.x"));
            }

            return this;
        }

        public TraceLog Build(InputPlan plan, bool complete = true, string? fingerprint = null)
        {
            var meta = new TraceMeta("ikemenlab.xray.trace/0", "test", "0.3", "t", "other", new Dictionary<string, bool>(), ["hook:loop"], fingerprint ?? InputPlanner.Fingerprint(plan));
            IEnumerable<TraceEvent> events = new TraceEvent[] { meta }.Concat(_events);
            if (complete) events = events.Append(new DriverEvent(_f, null, "plan_complete", null, null)).Append(new EndEvent(_f, null, "planComplete"));
            var list = events.ToList();
            return new TraceLog { Meta = meta, Events = list, Issues = [], LineCount = list.Count };
        }
    }

    /// <summary>Neutral, then "x" as planned and P1 in 200 from frame 6, hitting at frame 8 (P2 knocked 120 away).</summary>
    private static Trace Opener(InputPlan plan)
    {
        var t = new Trace().Driver("plan_start", detail: plan.RouteKey).Frames(3, 0);
        t.Driver("step_wait", 1, plan.Steps[0].EdgeId).Driver("step_ready", 1, plan.Steps[0].EdgeId).Input(1, "x").Frames(2, 0).Input(1).Frames(2, 200);
        return t.Driver("step_done", 1, "200").Frames(10, 200, 5000, "H", 950, 1, 120).Frames(8, 0, 0, "I", 950, 0, 120);
    }

    [Fact]
    public void AChaseThatNeverGetsCloseIsOutOfReach()
    {
        var plan = Planned("200 > chase:40 > 220").Plan!;
        var t = Opener(plan).Driver("step_wait", 2, plan.Steps[1].EdgeId).Driver("step_ready", 2, plan.Steps[1].EdgeId).Frames(301, 0, life: 950, distance: 120);
        var r = SequenceVerifier.Verify(plan, t.Driver("step_timeout", 2, "never came within 40").Build(plan, complete: false));
        Assert.Equal((SequenceVerdict.DidNotConnect, SequenceReason.OutOfReach, 2), (r.Verdict, r.Reason, r.FailedStep));
        Assert.Contains("closest 120", r.Steps[1].Why);
        Assert.Equal(SequenceStepOutcome.NotReached, r.Steps[2].Outcome);
    }

    [Fact]
    public void ARunThatStopsBeforeTheFirstMoveIsNeverCalledADidNotConnect()
    {
        var plan = Planned("200 > 210").Plan!;
        var stuck = new Trace().Driver("plan_start", detail: plan.RouteKey).Frames(30, 5000, life: 1000);
        var r = SequenceVerifier.Verify(plan, stuck.Driver("timeout", detail: "P1 never became free").End("neutralTimeout").Build(plan, complete: false));
        Assert.Equal(SequenceVerdict.CouldNotTest, r.Verdict);
        Assert.Equal(1, r.FailedStep);
        Assert.Contains("was never attempted", r.Summary);
    }

    [Fact]
    public void ADriverClaimTheSamplesContradictIsNotTrusted()
    {
        var plan = Planned("200 > chase:40 > 220").Plan!;
        var t = Opener(plan).Driver("step_wait", 2, plan.Steps[1].EdgeId).Driver("step_ready", 2, plan.Steps[1].EdgeId).Frames(5, 0, life: 950, distance: 120);
        var r = SequenceVerifier.Verify(plan, t.Driver("step_done", 2, "reached 30").Frames(5, 0, life: 950, distance: 120).Build(plan));
        Assert.Equal((SequenceVerdict.CouldNotTest, SequenceReason.DriverDisagrees), (r.Verdict, r.Reason));
    }

    [Fact]
    public void AForeignRecordingOrAMissingMoveIsNeverAVerdictAboutTheSequence()
    {
        var plan = Planned("200 > 210").Plan!;
        Assert.Equal((SequenceVerdict.CouldNotTest, VerifyReason.PlanMismatch),
            (SequenceVerifier.Verify(plan, Opener(plan).Build(plan, fingerprint: "OTHER")).Verdict, SequenceVerifier.Verify(plan, Opener(plan).Build(plan, fingerprint: "OTHER")).Reason));

        // The follow-up's input is fed during the move, but P1 goes into a different move: the follow-up did not happen.
        var wrong = new Trace().Driver("plan_start", detail: plan.RouteKey).Frames(3, 0);
        wrong.Driver("step_wait", 1, plan.Steps[0].EdgeId).Driver("step_ready", 1, plan.Steps[0].EdgeId).Input(1, "x").Frames(2, 0).Input(1).Frames(2, 200);
        wrong.Driver("step_done", 1, "200").Frames(4, 200, 5000, "H", 950, 1);
        wrong.Driver("step_wait", 2, plan.Steps[1].EdgeId).Driver("step_ready", 2, plan.Steps[1].EdgeId).Input(2, "y").Frames(2, 200, 5000, "H", 950, 1).Input(2);
        wrong.Frames(8, 220, 5000, "H", 950);
        var r = SequenceVerifier.Verify(plan, wrong.Build(plan));
        Assert.Equal((SequenceVerdict.DidNotConnect, VerifyReason.WrongState, 2), (r.Verdict, r.Reason, r.FailedStep));
        Assert.Equal(SequenceStepOutcome.Failed, r.Steps[1].Outcome);
        Assert.Contains("did not happen: you went into State 220", r.Steps[1].Why);
        Assert.NotNull(r.Steps[1].Evidence);                                         // the route verifier's structured evidence comes along
    }

    // ------------------------------------------------------------------ names, store, experiments, session

    [Fact]
    public void RenamedAbilitiesAppearInEveryStepLabelAndSummary()
    {
        var names = CharacterNames.Load(new NameOverlayStore(Temp("xray-seq-names-")), _index, Path.Combine(_fx.Root, "chars", "ComboGuy"));
        Assert.Null(names.Rename("ability:200", "Jab"));
        Assert.Null(names.Rename("ability:210", "Follow Kick"));
        var seq = Seq("200 > chase:40 > 210");
        Assert.Equal("Jab → Chase until within 40 → Follow Kick", SequenceLabels.Chain(seq.Actions, _index));
        var planned = SequencePlanner.Plan(_g, seq, 60);
        Assert.Equal(["Jab", "Chase until within 40", "Follow Kick"], planned.Steps.Select(s => s.Label));
        var cancel = SequencePlanner.Plan(_g, Seq("200 > 210"), 60);
        Assert.StartsWith("Cancel Jab into it", cancel.Steps[1].How);                // the linked name of state 200

        var (r, _) = Run("seq_far");
        Assert.Contains("Follow", SequenceLabels.Label(SequenceAction.Ability("ability:210"), _index));
        Assert.Equal("Jab", SequenceLabels.Label(SequenceAction.Ability("ability:200"), _index));
        Assert.Equal("Jab", r.Steps[0].Label);                                      // labels taken at planning time flow into the report
        Assert.Contains("ability:200", SequenceSpec.Format(seq.Actions));            // the ids stay underneath
    }

    [Fact]
    public void SavedSequencesKeepAVersionThatChangesOnlyWithTheSteps()
    {
        var store = new SequenceStore(Temp("xray-seq-store-"));
        var folder = Path.Combine(_fx.Root, "chars", "ComboGuy");
        var a = store.Save(folder, Seq("200 > 210") with { Id = "seq-a", Name = "Jab into kick" });
        Assert.Equal(1, a.Version);
        Assert.Equal(1, store.Save(folder, a with { Name = "Renamed" }).Version);    // a new name is not a new version
        var b = store.Save(folder, a with { Name = "Renamed", Actions = SequenceSpec.Parse("200 > wait:4 > 210") });
        Assert.Equal(2, b.Version);
        Assert.Equal("seq-a@v2", b.Key);
        var saved = Assert.Single(store.Load(folder).Sequences);
        Assert.Equal(("Renamed", "ability:200 > wait:4 > ability:210"), (saved.Name, saved.Spec));
        Assert.DoesNotContain(folder, store.PathFor(folder));                         // app data, keyed by a hash; never the character folder
        store.Delete(folder, "seq-a");
        Assert.Empty(store.Load(folder).Sequences);
    }

    private sealed class FixtureRunner(Func<string, string> traceForPlan) : ICancellableEngineRunner
    {
        public int Runs;
        public Action<int, CancellationToken>? During;
        public readonly List<string> Sandboxes = [];
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
        {
            Runs++;
            Sandboxes.Add(sandbox.Root);
            During?.Invoke(Runs, cancel);
            cancel.ThrowIfCancellationRequested();
            File.WriteAllText(sandbox.TracePath, traceForPlan(File.ReadAllText(Path.Combine(sandbox.Root, "external", "mods", "xray_plan.lua"))));
            return new EngineRunResult(0, false, null);
        }
    }

    private string FakeInstall()
    {
        var root = Temp("xray-seq-inst-");
        void W(string rel, string text)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("Ikemen_GO.exe", "exe");
        W("data/select.def", "[Characters]\nkfm\n\n[ExtraStages]\nstages/ring.def\n");
        W("external/script/main.lua", "main()\n");
        W("chars/ComboGuy/ComboGuy.def", "[Info]\n[Files]\ncns = x.cns\n");
        W("chars/kfm/kfm.def", "[Info]\n[Files]\ncns = x.cns\n");
        W("stages/ring.def", "[StageInfo]\n");
        var engine = Path.Combine(Temp("xray-seq-eng-"), "xray_engine.exe");
        File.WriteAllText(engine, new string('x', 3000) + PlaybackPreflight.HookName + new string('y', 3000));
        File.WriteAllText(Path.Combine(root, "engine.path"), engine);
        return root;
    }

    private (ComboPlaybackService Playback, ExperimentStore Store, SequenceRunRequest Request, ExperimentScope Scope, FixtureRunner Runner, string PlaybackStore, string Sandboxes)
        Experiment(string scenario)
    {
        var root = FakeInstall();
        var planned = Planned(MockSequences[scenario]);
        var runner = new FixtureRunner(_ => FixtureText(scenario, planned.Plan!));
        var playbackStore = Temp("xray-seq-playback-");
        var sandboxes = Temp("xray-seq-sbx-");
        var playback = new ComboPlaybackService(runner, playbackStore, sandboxes);
        var setup = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = File.ReadAllText(Path.Combine(root, "engine.path")) });
        Assert.True(setup.Ready, string.Join(" | ", setup.Issues));
        var request = new SequenceRunRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", planned.Plan!, planned.Steps.Select(s => s.Label).ToList(), setup, "test sequence");
        var scope = new ExperimentScope(_index.CharacterId, ExperimentScope.HashOf(_index), null, setup.Dummy, setup.Stage, setup.ApproachDistance, "seq-test", 1, "Test",
            InputPlanner.Fingerprint(planned.Plan!));
        return (playback, new ExperimentStore(Temp("xray-seq-exp-")), request, scope, runner, playbackStore, sandboxes);
    }

    [Fact]
    public void ExperimentsNeverTouchThePlaybackHistoryEvenPastTheKeep25Limit()
    {
        var (playback, store, request, scope, runner, playbackStore, sandboxes) = Experiment("seq_true");
        for (var i = 0; i < ComboPlaybackService.KeepRecords; i++)                 // a full playback history the user cares about
        {
            var d = Directory.CreateDirectory(Path.Combine(playbackStore, $"20260101-0000{i:00}-keep{i:00}")).FullName;
            File.WriteAllText(Path.Combine(d, "meta.json"), $"{{\"id\":\"keep{i:00}\",\"createdUtc\":\"2026-01-01T00:00:{i:00}Z\",\"status\":\"Verified\"}}");
        }

        var before = Directory.GetDirectories(playbackStore).OrderBy(x => x).ToList();
        var outcome = SequenceExperimentRunner.Run(playback, store, request, scope, "x → y", ComboPlaybackService.KeepRecords + 5, new PlaybackCancellation());

        Assert.Equal(before, Directory.GetDirectories(playbackStore).OrderBy(x => x).ToList());   // untouched: nothing added, nothing evicted
        Assert.Equal(30, outcome.Summary.Completed);
        Assert.Equal(30, Directory.GetDirectories(Path.Combine(outcome.Summary.Directory, "trials")).Length);   // every trial kept in the experiment
        Assert.StartsWith(store.Root, outcome.Summary.Directory);
        Assert.Empty(Directory.GetDirectories(sandboxes));
        Assert.Equal((30, 30, 1.0, 1.0), (outcome.Summary.Successes, outcome.Summary.TrueCombos, outcome.Summary.SuccessRate!.Value, outcome.Summary.ConnectionRate!.Value));
        Assert.NotNull(outcome.Summary.Scope.EngineSha256 ?? "mock traces carry no engine hash");
        Assert.Equal(30, runner.Runs);
        var listed = Assert.Single(store.List(scope.CharacterHash, "seq-test"));
        Assert.Equal(30, listed.Trials.Count);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(outcome.Summary.Directory, "experiment.json")));
        Assert.Equal(ExperimentSummary.SchemaVersion, json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1.0, json.RootElement.GetProperty("stats").GetProperty("trueComboRate").GetDouble());
    }

    [Fact]
    public void CancellingAnExperimentKeepsFinishedTrialsStopsTheRestAndCleansUp()
    {
        var (playback, store, request, scope, runner, playbackStore, sandboxes) = Experiment("seq_gap");
        var job = new PlaybackCancellation();
        runner.During = (n, cancel) => { if (n == 3) { job.Cancel(); cancel.WaitHandle.WaitOne(2000); } };
        var outcome = SequenceExperimentRunner.Run(playback, store, request, scope, "x → walk → z", 10, job);
        Assert.True(outcome.Summary.Stopped);
        Assert.Equal((10, 2), (outcome.Summary.Requested, outcome.Summary.Completed));
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(outcome.Summary.Directory, "trials")).Length);   // the cancelled trial left no record
        Assert.Equal(3, runner.Runs);
        Assert.All(runner.Sandboxes, s => Assert.False(Directory.Exists(s)));
        Assert.Empty(Directory.GetDirectories(playbackStore));
        Assert.Contains(ExperimentText.Lines(outcome.Summary), l => l.StartsWith("Stopped by you after 2 of 10"));

        // Cancelled before any trial finished: nothing is published and the experiment folder is removed.
        var (p2, store2, request2, scope2, runner2, _, _) = Experiment("seq_gap");
        var job2 = new PlaybackCancellation();
        runner2.During = (_, cancel) => { job2.Cancel(); cancel.WaitHandle.WaitOne(2000); };
        Assert.Throws<OperationCanceledException>(() => SequenceExperimentRunner.Run(p2, store2, request2, scope2, "s", 5, job2));
        Assert.Empty(store2.List());
    }

    [Fact]
    public void ExperimentStatisticsCountFailuresByStepAndCompareOnlyLikeWithLike()
    {
        var (playback, store, request, scope, _, _, _) = Experiment("seq_far");
        var far = SequenceExperimentRunner.Run(playback, store, request, scope, "far", 3, new PlaybackCancellation()).Summary;
        Assert.Equal((0, 0.0), (far.Successes, far.SuccessRate!.Value));
        var label = SequenceLabels.Label(SequenceAction.Ability("ability:220"), _index);   // X-Ray's name for it (or yours, when renamed)
        var (reason, count) = Assert.Single(far.FailureReasons);
        Assert.Equal(($"Step 3 ({label}): NoContact", 3), (reason, count));
        Assert.Equal($"Step 3 ({label}): the attack did not touch the opponent", ExperimentText.Plain(reason));
        Assert.Contains(ExperimentText.Lines(far), l => l.StartsWith($"Did not work — Step 3 ({label}): the attack did not touch the opponent: 3×"));

        var (p2, store2, request2, scope2, _, _, _) = Experiment("seq_true");
        var good = SequenceExperimentRunner.Run(p2, store2, request2, scope2 with { SequenceVersion = 2 }, "true", 2, new PlaybackCancellation()).Summary;
        var (rows, warning) = ExperimentText.Compare(far, good);
        Assert.Null(warning);                                                          // same character files, engine, dummy, stage, spacing
        Assert.Contains(rows, r => r.Metric == "Succeeded" && r.A.StartsWith("0 ") && r.B.StartsWith("2 "));
        Assert.NotNull(ExperimentText.Compare(far, good with { Scope = good.Scope with { Dummy = "someone-else" } }).Warning);
        Assert.Contains("×50 launches IKEMEN 50 time(s)", SequenceExperimentRunner.CostText(50, 12));
        Assert.Contains("about 10 minute(s)", SequenceExperimentRunner.CostText(50, 12));
    }

    [Fact]
    public async Task ASequenceRunsThroughTheOneSessionScopedToItsExactVersion()
    {
        var (playback, store, request, scope, _, _, _) = Experiment("seq_chase");
        var session = new PlaybackSession(playback);
        var job = new SequenceJob("seq-test@v1", "Test", request.Setup, 1, "test");
        await session.RunSequenceAsync(job, (gate, progress) => SequenceExperimentRunner.Run(playback, store, request, scope, "chase", 1, gate, progress));
        Assert.Equal((PlaybackState.Finished, AttemptState.VerdictProduced, PlaybackMode.Sequence), (session.State, session.Attempt, session.RunMode));
        Assert.Equal("sequence:seq-test@v1", session.RunRouteKey);
        Assert.True(session.HasResultFor("sequence:seq-test@v1"));
        Assert.False(session.HasResultFor("sequence:seq-test@v2"));                   // another version of the same sequence never shows it
        Assert.Null(session.Outcome);
        Assert.StartsWith("Connected sequence", session.Headline);
        Assert.Null(session.DiagnosticFor("sequence:seq-test@v1"));                    // the Sequence Lab has its own details view
        Assert.False(session.CanInspect);
        Assert.Equal(VerdictWaitKind.Verdict, session.VerdictForLatestAttempt().Kind);
    }

    /// <summary>
    /// Real runs on the installed IKEMEN build (kfm vs kfm, approach 40) from the Phase 3 Windows acceptance pass, re-judged by the current verifier. Each
    /// plan's fingerprint must equal the one its engine run recorded. These pin two defects the real engine exposed: the move-only projection dropping the
    /// driver's plan_complete (it said Could Not Test for a plain whiff), and failure reasons that did not say what really happened.
    /// </summary>
    [Fact]
    public void RealKfmSequencesOnTheInstalledEngineReadAsTheyWereObserved()
    {
        static (InputPlan Plan, TraceLog Log, List<string> Labels) Load(string name)
        {
            var plan = InputPlanner.FromJson(File.ReadAllText(Path.Combine(FixtureDir, $"phase3_kfm_{name}_real_plan.json")));
            var log = TraceReader.ReadFile(Path.Combine(FixtureDir, $"phase3_kfm_{name}_real_trace.jsonl"));
            Assert.Equal(log.Meta!.PlanFingerprint, InputPlanner.Fingerprint(plan));
            Assert.Equal("F68DC97081E9C14A348FF95EEBE52A365D2EA67357AEAC8C94BFD3069123D0A1", log.Meta.EngineSha256);
            return (plan, log, JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(FixtureDir, $"phase3_kfm_{name}_real_labels.json")))!);
        }

        // x → y straight away: y was pressed inside the hit pause, so it never started; the reason says so and suggests a wait.
        var (p1, l1, n1) = Load("xy_nowait");
        var a = SequenceVerifier.Verify(p1, l1, n1);
        Assert.Equal((SequenceVerdict.DidNotConnect, VerifyReason.WrongState, 2), (a.Verdict, a.Reason, a.FailedStep));
        Assert.Contains("back in neutral at frame 293", a.Steps[1].Why);
        Assert.Contains("hit pause after the previous hit (frames 276–284", a.Steps[1].Why);
        Assert.Contains("try a Wait of about 6 frames", a.Steps[1].Why);

        // x → wait 12 → y: a true combo; the opponent never left hitstun from 276 to 297.
        var (p2, l2, n2) = Load("xy_wait12");
        var b = SequenceVerifier.Verify(p2, l2, n2);
        Assert.Equal(SequenceVerdict.TrueCombo, b.Verdict);
        Assert.Equal((276L, 297L, 0, 80.0), (b.FirstContactFrame!.Value, b.LastContactFrame!.Value, b.OutOfHitstunFrames!.Value, b.Damage!.Value));

        // Kung Fu Palm → chase to 35 → x: the chase arrived, but the opponent was lying down; x passed over them.
        var (p3, l3, n3) = Load("palm_chase_lying");
        var c = SequenceVerifier.Verify(p3, l3, n3);
        Assert.Equal((SequenceVerdict.DidNotConnect, SequenceReason.NoContact, 3), (c.Verdict, c.Reason, c.FailedStep));
        Assert.Contains("lying down", c.Steps[2].Why);
        Assert.Contains("could act again at frame 430", c.Steps[2].Why);
        Assert.Equal(SequenceStepOutcome.Done, c.Steps[1].Outcome);

        // Kung Fu Palm → chase → wait 20 → x (through the app): it connects as they stand — a connected sequence, not a combo.
        var (p4, l4, n4) = Load("palm_chase_wait");
        var d = SequenceVerifier.Verify(p4, l4, n4);
        Assert.Equal(SequenceVerdict.ConnectedSequence, d.Verdict);
        Assert.Equal((18, 4), (d.OutOfHitstunFrames!.Value, d.OpponentCouldActFrames!.Value));
        Assert.Equal((true, 434L), (d.Steps[3].Connected!.Value, d.Steps[3].ContactFrame!.Value));
        Assert.Equal(113, d.Damage);
    }

    [Fact]
    public void TheSituationAfterPlayAbilityIsTakenWhenYouCanActAgain()
    {
        static InputPlan Plan(string name) => InputPlanner.FromJson(File.ReadAllText(Path.Combine(FixtureDir, name)));
        var r = AbilityVerifier.Verify("ability:1000", Plan("phase2_kfm_ability1000_real_plan.json"), TraceReader.ReadFile(Path.Combine(FixtureDir, "phase2_kfm_ability1000_real_trace.jsonl")));
        var s = r.Observed!.Situation!;
        Assert.Equal((329L, "when you could act again"), (s.Frame, s.When));
        Assert.Equal(true, s.YouCanAct);
        Assert.Contains("launched", s.OpponentPosture);                                  // still falling when kfm recovered
        Assert.StartsWith("Situation when you could act again (frame 329): the opponent is launched", s.Describe());
        Assert.Contains(AbilityText.Details(r.Observed, null), l => l.StartsWith("Situation when you could act again"));
    }
}
