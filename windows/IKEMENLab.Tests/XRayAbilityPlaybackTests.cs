using System.Text.Json;
using System.Text.RegularExpressions;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Phase 2: Play Ability (one ability from neutral through its own command, judged by the route verifier's step check) and State Preview (a forced
/// state, never proof). Both run through the M3/M4 pipeline; these tests pin the evidence semantics as well as the plumbing.
/// </summary>
public class XRayAbilityPlaybackTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly SemanticIndex _index;
    private readonly CandidateGraph _g;
    private readonly List<string> _cleanup = [];

    public XRayAbilityPlaybackTests()
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

    private AbilityPath PathFor(string abilityId)
    {
        var r = AbilityPlayback.Resolve(_g, abilityId);
        Assert.True(r.Path is not null, r.Refused);
        return r.Path!;
    }

    private InputPlan AbilityPlan(string abilityId, int approach = 60)
    {
        var path = PathFor(abilityId);
        return InputPlanner.Plan(_g, path.Route, AbilityPlayback.PlanOptionsFor(_g, path.Route, approach)).Plan!;
    }

    private InputPlan PreviewPlan(string stateId, int approach = 60) =>
        StatePreview.Plan(_index, stateId, approach, AbilityPlayback.TailFramesFor(StatePreview.AnimTicks(_index, stateId))).Plan!;

    /// <summary>Regenerates the plans the mock engine plays for the Phase 2 fixtures: IKEMENLAB_DUMP_ABILITY_PLAN=a.lua IKEMENLAB_DUMP_PREVIEW_PLAN=p.lua dotnet test --filter DumpPhase2MockPlans.</summary>
    [Fact]
    public void DumpPhase2MockPlans()
    {
        if (Environment.GetEnvironmentVariable("IKEMENLAB_DUMP_ABILITY_PLAN") is { Length: > 0 } a) File.WriteAllText(a, InputPlanner.ToLua(AbilityPlan("ability:200")));
        if (Environment.GetEnvironmentVariable("IKEMENLAB_DUMP_PREVIEW_PLAN") is { Length: > 0 } p) File.WriteAllText(p, InputPlanner.ToLua(PreviewPlan("state:1000")));
    }

    // ------------------------------------------------------------------ which command path

    [Fact]
    public void PlayAbilityPressesTheAbilitysOwnCommandFromNeutral()
    {
        var x = PathFor("ability:200");
        Assert.Equal("x", x.Command);
        Assert.Equal((CandidateGraph.NeutralId, "state:200", EdgeKind.Start), (x.Edge.From, x.Edge.To, x.Edge.Kind));
        Assert.Single(x.Route.Steps);
        Assert.Equal(CandidateGraph.NeutralId, x.Route.StartState);
        Assert.Empty(x.Warnings);

        var special = PathFor("ability:1000");
        Assert.Equal("QCF_x", special.Command);
        Assert.Equal("state:1000", special.EntryStateId);
    }

    [Fact]
    public void AbilitiesWithoutACommandFromNeutralAreRefusedWithTheReasonAndOfferAPreview()
    {
        var super = AbilityPlayback.Resolve(_g, "ability:3000");     // only entered as a cancel out of 1000 on hit
        Assert.Null(super.Path);
        Assert.Contains("only entered from other moves", super.Refused);
        Assert.Contains("not proof", super.Refused);
        Assert.True(super.CanPreview);

        Assert.Contains("is not an ability", AbilityPlayback.Resolve(_g, "state:200").Refused);
        Assert.Contains("is not an ability", AbilityPlayback.Resolve(_g, "ability:9999").Refused);
    }

    [Fact]
    public void AiOnlyAbilitiesAreRefusedAndPowerGatesAreWarned()
    {
        var fv = CandidateGraph.Build(CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine));
        var throwAi = AbilityPlayback.Resolve(fv, "ability:1500");
        Assert.Null(throwAi.Path);
        Assert.Contains("entered only by the AI", throwAi.Refused);

        var love = AbilityPlayback.Resolve(fv, "ability:10800");
        Assert.NotNull(love.Path);
        Assert.Contains(love.Path!.Warnings, w => w.Contains("power >= 3000"));
    }

    [Fact]
    public void AnAbilityPlanIsTheRoutePlanOfOneStepWithALongerWatchAndNoForce()
    {
        var plan = AbilityPlan("ability:200");
        Assert.Single(plan.Steps);
        Assert.Null(plan.Steps[0].FromState);
        Assert.Equal(200, plan.Steps[0].ToState);
        Assert.False(plan.Steps[0].IsForce);
        Assert.Equal(AbilityPlayback.TailFramesFor(12), plan.TailFrames);     // anim 200 lasts 12 ticks → clamped to the 120-tick floor
        Assert.Equal(120, plan.TailFrames);
        Assert.DoesNotContain("force", InputPlanner.ToLua(plan));
    }

    [Fact]
    public void RoutePlanLuaIsByteForByteWhatItWasBeforePreviewsExisted()
    {
        var route = ComboSearch.Find(_g, new ComboOptions { MaxMoves = 2, Top = 500 }).Routes.First(r => r.States.SequenceEqual(["state:200", "state:210"]));
        var lua = InputPlanner.ToLua(InputPlanner.Plan(_g, route).Plan!);
        Assert.DoesNotContain("force", lua);
        Assert.Contains("earliestTick = nil, timeout = 45,\n      input = {", lua);
    }

    // ------------------------------------------------------------------ the ability verdict

    private sealed class Trace
    {
        private long _f;
        private readonly List<TraceEvent> _events = [];

        public Trace Driver(string kind, int? step = null, string? detail = null) { _events.Add(new DriverEvent(_f, null, kind, step, detail)); return this; }
        public Trace Input(int step, params string[] keys) { _events.Add(new InputEvent(_f, null, 1, keys, step, "input")); return this; }

        public Trace Frames(int n, int p1, bool? p1Ctrl = null, int p2 = 0, string p2Move = "I", double p2Life = 1000, int hit = 0, string p2Type = "S")
        {
            for (var i = 0; i < n; i++)
            {
                _f++;
                var a = new PlayerSample(p1, null, p1Ctrl ?? p1 == 0, "S", p1 == 0 ? "I" : "A", null, null, 1000, 0, 0, 0, null, null, 1, hit, hit, null);
                var b = new PlayerSample(p2, null, p2 == 0, p2Type, p2Move, null, null, p2Life, 0, 40, 0, null, null, -1, 0, 0, null);
                _events.Add(new FrameEvent(_f, null, 1, a, b, 40, null, null, null, "derived:p2.x-p1.x"));
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

    /// <summary>Neutral, the "x" input as planned (x, x, release), then P1 in 200 from frame 6. The caller writes what follows.</summary>
    private static Trace StartsX()
    {
        var t = new Trace().Driver("plan_start").Frames(3, 0);
        t.Input(1, "x").Frames(2, 0).Input(1).Frames(1, 200);
        return t;
    }

    [Fact]
    public void AnAbilityThatStartsFromItsCommandIsPerformedAndWhatFollowedIsMeasured()
    {
        var plan = AbilityPlan("ability:200");
        var t = StartsX().Frames(1, 200);                                    // frames 6–7: the move started, nothing touched yet
        t.Frames(10, 200, p2: 5000, p2Move: "H", p2Life: 980, hit: 1);       // frames 8–17: it hit for 20
        t.Frames(2, 200, p2Life: 980, hit: 1);                               // frames 18–19: the opponent recovered
        t.Frames(30, 0, p2Life: 980);                                        // frame 20 on: P1 has control again

        var r = AbilityVerifier.Verify("ability:200", plan, t.Build(plan));
        Assert.Equal(AbilityStatus.Performed, r.Status);
        Assert.Null(r.Reason);
        Assert.Equal((3L, 6L), (r.InputFrame!.Value, r.EntryFrame!.Value));
        Assert.Equal([AbilityVerifier.TransitionRule], r.RuntimeRules);
        Assert.Equal((nameof(VerifyStatus.Failed), VerifyReason.ComboDropped), (r.RouteVerifierStatus, r.RouteVerifierReason));   // a combo verdict, kept but not used

        var o = r.Observed!;
        Assert.True(o.ObservationComplete);
        Assert.Equal("yes", o.Connected);
        Assert.Equal((true, true, 8L), (o.OwnHit, o.OpponentHit, o.ContactFrame!.Value));
        Assert.Equal(20, o.Damage);
        Assert.Equal((14, 20L), (o.FramesUntilControl!.Value, o.ControlFrame!.Value));
        Assert.Equal([200, 0], o.P1States);
        Assert.Equal(false, o.OpponentKnockedDown);
        Assert.StartsWith("Performed · State 200 started via “x” · connected · 20 damage · back in control after 14f", AbilityText.Headline(r, null));
    }

    [Fact]
    public void AWhiffIsStillPerformedAndSaysItDidNotConnect()
    {
        var plan = AbilityPlan("ability:200");
        var t = StartsX().Frames(12, 200).Frames(30, 0);
        var r = AbilityVerifier.Verify("ability:200", plan, t.Build(plan));
        Assert.Equal(AbilityStatus.Performed, r.Status);
        Assert.Equal("no", r.Observed!.Connected);
        Assert.Equal(0, r.Observed.Damage);

        // Without the driver's completed watch, absence of contact is not shown.
        var cut = AbilityVerifier.Verify("ability:200", plan, StartsX().Frames(4, 200).Build(plan, complete: false));
        Assert.Equal("unknown", cut.Observed!.Connected);
        Assert.False(cut.Observed.ObservationComplete);
    }

    [Fact]
    public void EvenWhenTheRouteVerifierCallsTheOneStepRunVerifiedTheAbilityCitesOnlyTheTransition()
    {
        var plan = AbilityPlan("ability:200");
        var t = new Trace().Driver("plan_start").Frames(3, 0);
        t.Input(1, "x").Frames(2, 0).Input(1);
        t.Frames(8, 200, p2: 5000, p2Move: "H", p2Life: 980, hit: 1).Frames(20, 0, p2Life: 980);   // P2 already in hitstun on the entry frame
        var log = t.Build(plan);
        Assert.Equal(VerifyStatus.Verified, RouteVerifier.Verify(plan, log).Status);

        var r = AbilityVerifier.Verify("ability:200", plan, log);
        Assert.Equal(AbilityStatus.Performed, r.Status);
        Assert.Equal([AbilityVerifier.TransitionRule], r.RuntimeRules);            // never runtime.opponent-continuous for one move
        var json = AbilityVerifier.ToJson(r);
        Assert.DoesNotContain("RuntimeVerified", json);
        Assert.DoesNotContain("routeConfidence", json);
        Assert.DoesNotContain("opponent-continuous", json);
    }

    [Fact]
    public void ADifferentMoveOrNoMoveIsNotPerformedAndABrokenRunIsInconclusive()
    {
        var plan = AbilityPlan("ability:200");

        var wrong = new Trace().Driver("plan_start").Frames(3, 0);
        wrong.Input(1, "x").Frames(2, 0).Input(1).Frames(10, 210);
        var w = AbilityVerifier.Verify("ability:200", plan, wrong.Build(plan));
        Assert.Equal((AbilityStatus.NotPerformed, VerifyReason.WrongState), (w.Status, w.Reason));
        Assert.Empty(w.RuntimeRules);
        Assert.Null(w.Observed);
        Assert.StartsWith("Not performed: the character went into a different move", AbilityText.Headline(w, null));

        var nothing = new Trace().Driver("plan_start").Frames(3, 0);
        nothing.Input(1, "x").Frames(2, 0).Input(1).Frames(60, 0);
        var none = AbilityVerifier.Verify("ability:200", plan, nothing.Build(plan));
        Assert.Equal((AbilityStatus.NotPerformed, VerifyReason.TransitionNotObserved), (none.Status, none.Reason));
        var why = AbilityVerifier.Inspect(none, RouteVerifier.Verify(plan, nothing.Build(plan)), nothing.Build(plan));
        Assert.StartsWith("Not performed: the next move never started", why!.Headline);

        var foreign = AbilityVerifier.Verify("ability:200", plan, StartsX().Frames(30, 0).Build(plan, fingerprint: "NOT-THIS-PLAN"));
        Assert.Equal((AbilityStatus.Inconclusive, VerifyReason.PlanMismatch), (foreign.Status, foreign.Reason));

        var noInput = new Trace().Driver("inject_unavailable", detail: "no injector is installed").Frames(5, 0);
        var n = AbilityVerifier.Verify("ability:200", plan, noInput.Build(plan, complete: false));
        Assert.Equal((AbilityStatus.Inconclusive, VerifyReason.InputInjectionUnavailable), (n.Status, n.Reason));

        var performed = StartsX().Frames(30, 0).Build(plan);
        Assert.Null(AbilityVerifier.Inspect(AbilityVerifier.Verify("ability:200", plan, performed), RouteVerifier.Verify(plan, performed), performed));
    }

    [Fact]
    public void OnlyAOneStepCommandPlanFromNeutralIsReadAsAnAbility()
    {
        var route = ComboSearch.Find(_g, new ComboOptions { MaxMoves = 2, Top = 500 }).Routes.First(r => r.States.SequenceEqual(["state:200", "state:210"]));
        var twoStep = InputPlanner.Plan(_g, route).Plan!;
        var r = AbilityVerifier.Verify("ability:200", twoStep, new Trace().Driver("plan_start").Frames(5, 0).Build(twoStep));
        Assert.Equal((AbilityStatus.Inconclusive, VerifyReason.PlanMismatch), (r.Status, r.Reason));
        var forced = PreviewPlan("state:1000");
        var asAbility = AbilityVerifier.Verify("ability:1000", forced, new Trace().Frames(3, 0).Build(forced));
        Assert.Equal((AbilityStatus.Inconclusive, VerifyReason.PlanMismatch), (asAbility.Status, asAbility.Reason));
        Assert.Empty(asAbility.RuntimeRules);
    }

    // ------------------------------------------------------------------ State Preview: never proof

    [Fact]
    public void OnlyTheCharactersOwnDefinedStatesCanBePreviewed()
    {
        Assert.True(StatePreview.CanPreview(_index, "state:1000", out _));
        Assert.False(StatePreview.CanPreview(_index, "state:-1", out var negative));
        Assert.Contains("always-running", negative);
        Assert.False(StatePreview.CanPreview(_index, "state:0", out var common));
        Assert.Contains("common", common);
        Assert.False(StatePreview.CanPreview(_index, "state:12345", out _));
        Assert.Null(StatePreview.Plan(_index, "state:-1", 60, 120).Plan);
    }

    [Fact]
    public void APreviewPlanForcesTheStateAndTheRouteVerifierRefusesToJudgeIt()
    {
        var plan = PreviewPlan("state:1000");
        var step = Assert.Single(plan.Steps);
        Assert.True(step.IsForce);
        Assert.Empty(step.Input);
        Assert.Null(step.Command);
        Assert.Equal("preview:state:1000", plan.RouteKey);
        Assert.Contains("force = true", InputPlanner.ToLua(plan));

        var report = RouteVerifier.Verify(plan, new Trace().Driver("plan_start").Frames(5, 0).Build(plan));
        Assert.Equal((VerifyStatus.Inconclusive, VerifyReason.PlanMismatch), (report.Status, report.Reason));
        Assert.NotEqual(Confidence.RuntimeVerified, report.RouteConfidence);
    }

    private static Trace UpToForce()
    {
        var t = new Trace().Driver("plan_start").Frames(25, 0);
        return t.Driver("step_wait", 1).Driver("step_ready", 1).Driver("force_applied", 1, "1000").Driver("tail_start");
    }

    [Fact]
    public void APreviewReportsWhatFollowedTheForcedStateAndIsNeverProof()
    {
        var plan = PreviewPlan("state:1000");
        var t = UpToForce().Frames(3, 1000).Frames(8, 1000, p2: 5000, p2Move: "H", p2Life: 950, hit: 1, p2Type: "L").Frames(30, 0, p2Life: 950);
        var r = StatePreview.Read(plan, t.Build(plan));
        Assert.Equal(PreviewStatus.Forced, r.Status);
        Assert.False(r.Proof);
        Assert.Equal((25L, 26L), (r.ForceFrame!.Value, r.FirstSeenFrame!.Value));
        Assert.Equal("yes", r.Observed!.Connected);
        Assert.Equal(true, r.Observed.OpponentKnockedDown);
        Assert.Equal(50, r.Observed.Damage);
        Assert.StartsWith("Preview (not proof) · State 1000 forced", StatePreview.Headline(r, null));

        using var doc = JsonDocument.Parse(StatePreview.ToJson(r));
        Assert.False(doc.RootElement.GetProperty("proof").GetBoolean());
        Assert.Contains("not proof", doc.RootElement.GetProperty("notProof").GetString());
        Assert.False(doc.RootElement.TryGetProperty("runtimeRules", out _));
        Assert.DoesNotContain("RuntimeVerified", StatePreview.ToJson(r));
    }

    [Fact]
    public void APreviewSaysWhenTheStateLeftAtOnceWasRefusedOrNeverForced()
    {
        var plan = PreviewPlan("state:1000");
        var gone = StatePreview.Read(plan, UpToForce().Frames(20, 1001).Frames(10, 0).Build(plan));
        Assert.Equal(PreviewStatus.ForcedNotSeen, gone.Status);
        Assert.Contains(gone.Notes, n => n.Contains("never sampled in State 1000"));

        var refused = new Trace().Driver("plan_start").Frames(25, 0).Driver("step_wait", 1).Driver("step_ready", 1).Driver("force_failed", 1, "1000");
        var rr = StatePreview.Read(plan, refused.Build(plan, complete: false));
        Assert.Equal((PreviewStatus.Refused, StatePreview.ForceRefused), (rr.Status, rr.Reason));
        Assert.StartsWith("Preview refused by the engine", StatePreview.Headline(rr, null));

        var stuck = StatePreview.Read(plan, new Trace().Driver("plan_start").Frames(30, 5000).Driver("timeout", detail: "P1 never became free").Build(plan, complete: false));
        Assert.Equal((PreviewStatus.Inconclusive, VerifyReason.DriverTimeout), (stuck.Status, stuck.Reason));
        Assert.Contains("P1 never became free", stuck.Detail);

        var foreign = StatePreview.Read(plan, UpToForce().Frames(5, 1000).Build(plan, fingerprint: "OTHER"));
        Assert.Equal((PreviewStatus.Inconclusive, VerifyReason.PlanMismatch), (foreign.Status, foreign.Reason));
    }

    // ------------------------------------------------------------------ through the M3/M4 service, session and diagnostics

    private sealed class FakeRunner(Func<string> trace) : IEngineRunner
    {
        public RuntimeSandbox? Seen;
        public string? PlanLua;
        public string? Config;
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout)
        {
            Seen = sandbox;
            PlanLua = File.ReadAllText(Path.Combine(sandbox.Root, "external", "mods", "xray_plan.lua"));
            Config = File.ReadAllText(Path.Combine(sandbox.Root, "external", "mods", "xray_config.lua"));
            File.WriteAllText(sandbox.TracePath, trace());
            return new EngineRunResult(0, false, null);
        }
    }

    private string FakeInstall()
    {
        var root = Temp("xray-p2-");
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
        var engine = Path.Combine(Temp("xray-p2-eng-"), "xray_engine.exe");
        File.WriteAllText(engine, new string('x', 3000) + PlaybackPreflight.HookName + new string('y', 3000));
        File.WriteAllText(Path.Combine(root, "engine.path"), engine);
        return root;
    }

    private PlaybackSetup Setup(string root)
    {
        var setup = PlaybackPreflight.Check(root, "ComboGuy", new AppSettings { XRayEnginePath = File.ReadAllText(Path.Combine(root, "engine.path")) });
        Assert.True(setup.Ready, string.Join(" | ", setup.Issues));
        return setup;
    }

    /// <summary>A trace the mock engine produced by running the real driver and probe on the plan (scripts/xray_driver_mock.py), re-identified to <paramref name="plan"/>.</summary>
    private static string Fixture(string scenario, InputPlan plan) =>
        Regex.Replace(File.ReadAllText(Path.Combine(FixtureDir, $"xray_driver_mock_{scenario}.jsonl")), "\"planFingerprint\":\"[0-9A-F]*\"", "\"planFingerprint\":\"" + InputPlanner.Fingerprint(plan) + "\"");

    private PlaybackRequest AbilityRequest(string root, string abilityId = "ability:200")
    {
        var path = PathFor(abilityId);
        return new PlaybackRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", _g, path.Route, Setup(root), "Play Ability · " + abilityId)
            { Mode = PlaybackMode.Ability, AbilityId = abilityId };
    }

    [Fact]
    public void PlayAbilityGoesThroughTheSameServiceAndKeepsTheRouteReportBesideTheAbilityResult()
    {
        var root = FakeInstall();
        var runner = new FakeRunner(() => Fixture("ability", AbilityPlan("ability:200")));
        var service = new ComboPlaybackService(runner, Temp("xray-p2-store-"), Temp("xray-p2-sbx-"));
        var outcome = service.Play(AbilityRequest(root), null, new PlaybackCancellation());

        Assert.Equal(AbilityStatus.Performed, outcome.Ability!.Status);
        Assert.Equal("Performed", outcome.Record.Status);
        Assert.Equal(("ability", "ability:200"), (outcome.Record.Mode, outcome.Record.TargetId));
        Assert.Null(outcome.Failure);                                                // nothing to inspect: the ability was performed
        Assert.Equal("yes", outcome.Ability.Observed!.Connected);
        Assert.True(outcome.Ability.Observed.ObservationComplete);
        Assert.NotNull(outcome.Ability.Observed.FramesUntilControl);
        foreach (var f in new[] { "plan.json", "trace.jsonl", "report.json", "ability.json", "meta.json" })
            Assert.True(File.Exists(Path.Combine(outcome.Record.Directory, f)), f);
        Assert.DoesNotContain("force", runner.PlanLua);
        Assert.Contains("lingerFrames = 90", runner.Config);                     // the engine window stays up on the result, as for Play Combo
        Assert.False(Directory.Exists(runner.Seen!.Root));                       // the sandbox is gone; the record is what remains

        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(outcome.Record.Directory, "meta.json")));
        Assert.Equal("ability", meta.RootElement.GetProperty("mode").GetString());
        var listed = Assert.Single(service.List());
        Assert.Equal(("ability", "ability:200", "Performed"), (listed.Mode, listed.TargetId, listed.Status));
    }

    [Fact]
    public void APreviewGoesThroughTheSameSandboxAndStoreButWritesNoVerificationReport()
    {
        var root = FakeInstall();
        var plan = PreviewPlan("state:1000");
        var runner = new FakeRunner(() => Fixture("preview", plan));
        var service = new ComboPlaybackService(runner, Temp("xray-p2-store-"), Temp("xray-p2-sbx-"));
        var outcome = service.Preview(new PreviewRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", _index, "state:1000", Setup(root), "preview"), null, new PlaybackCancellation());

        Assert.Equal(PreviewStatus.Forced, outcome.Report.Status);
        Assert.Equal(("preview", "state:1000", "Forced"), (outcome.Record.Mode, outcome.Record.TargetId, outcome.Record.Status));
        Assert.Contains("force = true", runner.PlanLua);
        Assert.True(File.Exists(outcome.Record.PreviewReportPath));
        Assert.False(File.Exists(outcome.Record.ReportPath));                       // a preview is never judged by the route verifier
        Assert.False(File.Exists(outcome.Record.AbilityReportPath));
        Assert.NotEmpty(outcome.Report.RunNotes);
        Assert.DoesNotContain(outcome.Report.Notes, n => n.StartsWith("Actual sandbox executable", StringComparison.Ordinal));
        Assert.False(Directory.Exists(runner.Seen!.Root));
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(outcome.Record.Directory, "meta.json")));
        Assert.False(meta.RootElement.GetProperty("proof").GetBoolean());

        var refused = new ComboPlaybackService(new FakeRunner(() => Fixture("preview_refused", plan)), Temp("xray-p2-store-"), Temp("xray-p2-sbx-"))
            .Preview(new PreviewRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", _index, "state:1000", Setup(root), "preview"), null, new PlaybackCancellation());
        Assert.Equal(PreviewStatus.Refused, refused.Report.Status);

        var leaves = new ComboPlaybackService(new FakeRunner(() => Fixture("preview_leaves", plan)), Temp("xray-p2-store-"), Temp("xray-p2-sbx-"))
            .Preview(new PreviewRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", _index, "state:1000", Setup(root), "preview"), null, new PlaybackCancellation());
        Assert.Equal(PreviewStatus.ForcedNotSeen, leaves.Report.Status);
    }

    [Fact]
    public void AStateThatCannotBeForcedIsRefusedBeforeAnySandboxOrRecord()
    {
        var root = FakeInstall();
        var store = Temp("xray-p2-store-");
        var runner = new FakeRunner(() => throw new InvalidOperationException("the engine must not run"));
        var service = new ComboPlaybackService(runner, store, Temp("xray-p2-sbx-"));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.Preview(new PreviewRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", _index, "state:-1", Setup(root), "preview"), null, new PlaybackCancellation()));
        Assert.Contains("always-running", ex.Message);
        Assert.Null(runner.Seen);
        Assert.Empty(Directory.EnumerateDirectories(store));
    }

    [Fact]
    public async Task AbilityAndPreviewShareTheOneSessionAndNeverShowAsAComboResult()
    {
        var root = FakeInstall();
        var abilityPlan = AbilityPlan("ability:200");
        var previewPlan = PreviewPlan("state:1000");
        var next = "ability";
        var runner = new FakeRunner(() => Fixture(next, next == "ability" ? abilityPlan : previewPlan));
        var session = new PlaybackSession(new ComboPlaybackService(runner, Temp("xray-p2-store-"), Temp("xray-p2-sbx-")));

        var request = AbilityRequest(root);
        await session.PlayAsync(request);
        Assert.Equal((PlaybackState.Finished, AttemptState.VerdictProduced), (session.State, session.Attempt));
        Assert.Equal("ability-play:ability:200", session.RunRouteKey);
        Assert.Equal(PlaybackMode.Ability, session.RunMode);
        Assert.False(session.IsFor(request.Route.Key));                              // the one-step route key never sees an ability result
        Assert.Equal(string.Empty, session.HeadlineFor(request.Route.Key));
        Assert.StartsWith("Performed · State 200 started via “x”", session.Headline);
        Assert.False(session.CanInspect);
        Assert.Equal(VerdictWaitKind.Verdict, session.VerdictForLatestAttempt().Kind);

        var ability = session.DiagnosticFor("ability-play:ability:200")!;
        Assert.Equal(("ability", nameof(AbilityStatus.Performed)), (ability.Mode, ability.Result!.Verdict));
        Assert.Null(ability.FailedStep);
        var abilityText = ability.ToText();
        Assert.StartsWith("IKEMEN Lab Play Ability diagnostic", abilityText);
        Assert.Contains("ability result: Performed", abilityText);
        Assert.Contains("route verifier on the one-step plan: Failed / ComboDropped", abilityText);
        Assert.DoesNotContain("(FAILED STEP)", abilityText);

        next = "preview";
        await session.PreviewAsync(new PreviewRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", _index, "state:1000", Setup(root), "preview"));
        Assert.Equal((PlaybackState.Finished, AttemptState.PreviewProduced), (session.State, session.Attempt));
        Assert.Null(session.Outcome);
        Assert.NotNull(session.PreviewResult);
        Assert.Null(session.Last);
        Assert.True(session.HasResultFor("preview:state:1000"));
        Assert.False(session.HasResultFor("ability-play:ability:200"));
        Assert.False(session.CanInspect);
        var wait = session.VerdictForLatestAttempt();
        Assert.Equal(VerdictWaitKind.Preview, wait.Kind);
        Assert.Contains("never a verdict", wait.Message);
        Assert.StartsWith("Preview (not proof)", session.Headline);

        var preview = session.DiagnosticFor("preview:state:1000")!;
        Assert.Equal((PlaybackDiagnostic.Kinds.Preview, "preview"), (preview.Kind, preview.Mode));
        Assert.Null(preview.Result!.Verdict);
        Assert.Empty(preview.Route);
        var previewText = preview.ToText();
        Assert.Contains("NOT PROOF", previewText);
        Assert.DoesNotContain("verdict:", previewText);
        Assert.Contains("\"proof\": false", preview.ToJson());

        var before = session.AttemptId;
        await session.ReplayAsync();                                                 // replays the preview, not the earlier ability run
        Assert.Equal((before + 1, AttemptState.PreviewProduced), (session.AttemptId, session.Attempt));
    }

    [Fact]
    public void ARefusedAbilityOrPreviewPressKeepsItsKindAndNeverBorrowsAVerdict()
    {
        var session = new PlaybackSession(new ComboPlaybackService(new FakeRunner(() => throw new InvalidOperationException("no engine run")), Temp("xray-p2-store-"), Temp("xray-p2-sbx-")));
        session.RefuseAttempt(AbilityPlayback.ScopeKey("ability:200"), "Playback needs an X-Ray engine build.");
        var ability = session.DiagnosticFor("ability-play:ability:200")!;
        Assert.Equal((PlaybackDiagnostic.Kinds.Refused, "ability"), (ability.Kind, ability.Mode));
        Assert.Null(ability.Result);
        Assert.StartsWith("IKEMEN Lab Play Ability diagnostic", ability.ToText());
        Assert.Equal(VerdictWaitKind.RefusedPreflight, session.VerdictForLatestAttempt().Kind);

        session.RefuseAttempt(StatePreview.ScopeKey("state:1000"), "Playback needs an X-Ray engine build.");
        var preview = session.DiagnosticFor("preview:state:1000")!;
        Assert.Equal((PlaybackDiagnostic.Kinds.Refused, "preview"), (preview.Kind, preview.Mode));
        Assert.Equal("state:1000", preview.Preview!.State);
        Assert.Null(preview.Preview.Status);
        Assert.Contains("NOT PROOF", preview.ToText());
        Assert.Contains("refused before any engine launch", preview.ToText());
    }

    [Fact]
    public async Task AComboDiagnosticGainsNoPhase2Fields()
    {
        var root = FakeInstall();
        var route = ComboSearch.Find(_g, new ComboOptions { MaxMoves = 3, Top = 500, MaxRepeats = 2 }).Routes.First(r => r.States.SequenceEqual(["state:200", "state:210", "state:1000"]));
        var plan = InputPlanner.Plan(_g, route).Plan!;
        var trace = File.ReadAllText(Path.Combine(FixtureDir, "xray_driver_mock_verified.jsonl")).Replace("\"planFingerprint\":null,", string.Empty)
            .Replace("\"type\":\"meta\"", "\"type\":\"meta\",\"planFingerprint\":\"" + InputPlanner.Fingerprint(plan) + "\"");
        var session = new PlaybackSession(new ComboPlaybackService(new FakeRunner(() => trace), Temp("xray-p2-store-"), Temp("xray-p2-sbx-")));
        await session.PlayAsync(new PlaybackRequest(root, "ComboGuy", "ComboGuy/ComboGuy.def", _g, route, Setup(root), "x → y → QCF"));
        Assert.Equal(VerifyStatus.Verified, session.Outcome!.Report.Status);
        Assert.Equal(route.Key, session.RunRouteKey);
        using var doc = JsonDocument.Parse(session.DiagnosticFor(route.Key)!.ToJson());
        foreach (var key in new[] { "mode", "ability", "preview" }) Assert.False(doc.RootElement.TryGetProperty(key, out _), key);
        Assert.StartsWith("IKEMEN Lab combo playback diagnostic", session.DiagnosticFor(route.Key)!.ToText());
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.Outcome.Record.Directory, "meta.json")));
        Assert.False(meta.RootElement.TryGetProperty("mode", out _));
    }

    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = IKEMENLab.Cli.CliApp.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void TheCliShowsThePathAndPlanOrWhyNotWithoutLaunchingAnything()
    {
        var target = Path.Combine(_fx.Root, "chars", "ComboGuy");
        var play = Cli("xray", "ability-plan", target, "200", "--root", _fx.Root, "--no-names");
        Assert.Equal(0, play.Code);
        using (var doc = JsonDocument.Parse(play.Out))
        {
            var r = doc.RootElement;
            Assert.Equal(("ikemenlab.xray.ability-plan/1", "ability", "ability:200", "x"),
                (r.GetProperty("schema").GetString(), r.GetProperty("mode").GetString(), r.GetProperty("ability").GetString(), r.GetProperty("command").GetString()));
            Assert.Equal("ikemenlab.xray.plan/1", r.GetProperty("plan").GetProperty("schema").GetString());
            Assert.Equal(1, r.GetProperty("plan").GetProperty("steps").GetArrayLength());
        }

        var refused = Cli("xray", "ability-plan", target, "ability:3000", "--root", _fx.Root, "--no-names");
        Assert.Equal(3, refused.Code);
        using (var doc = JsonDocument.Parse(refused.Out))
        {
            Assert.Contains("only entered from other moves", doc.RootElement.GetProperty("refused").GetString());
            Assert.True(doc.RootElement.GetProperty("canPreview").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("plan").ValueKind);
        }

        var preview = Cli("xray", "ability-plan", target, "ability:3000", "--preview", "--root", _fx.Root, "--no-names");
        Assert.Equal(0, preview.Code);
        using (var doc = JsonDocument.Parse(preview.Out))
        {
            Assert.False(doc.RootElement.GetProperty("proof").GetBoolean());
            Assert.Equal("Force", doc.RootElement.GetProperty("plan").GetProperty("steps")[0].GetProperty("kind").GetString());
        }

        Assert.Equal(2, Cli("xray", "runtime-ability", target, "200", "--root", _fx.Root, "--no-names").Code);   // no dummy/stage: refused before any sandbox
    }

    [Fact]
    public async Task TheAbilityHeadlineUsesTheNamesFrozenWhenTheAttemptStarted()
    {
        var root = FakeInstall();
        var names = CharacterNames.Load(new NameOverlayStore(Temp("xray-p2-names-")), _index, Path.Combine(_fx.Root, "chars", "ComboGuy"));
        Assert.Null(names.Rename("state:200", "Jab"));
        var session = new PlaybackSession(new ComboPlaybackService(new FakeRunner(() => Fixture("ability", AbilityPlan("ability:200"))), Temp("xray-p2-store-"), Temp("xray-p2-sbx-")));
        await session.PlayAsync(AbilityRequest(root));
        Assert.Null(names.Rename("state:200", "Renamed later"));                    // a later rename never changes this attempt
        Assert.StartsWith("Performed · Jab (State 200) started via “x”", session.Headline);
        Assert.Contains("state:200 \"Jab\"", session.DiagnosticFor("ability-play:ability:200")!.ToText());
    }
}
