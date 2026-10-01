using System.Text.Json;
using IKEMENLab.Cli;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Tests;

public class XRayVerifyTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly SemanticIndex _index;
    private readonly CandidateGraph _g;
    private readonly List<string> _cleanup = [];

    public XRayVerifyTests()
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

    private ComboRoute Route(params string[] states)
    {
        var res = ComboSearch.Find(_g, new ComboOptions { MaxMoves = states.Length, Top = 500, MaxRepeats = 2 });
        return res.Routes.First(r => r.States.Select(s => s.Replace("state:", "")).SequenceEqual(states));
    }

    private InputPlan PlanFor(params string[] states)
    {
        var planned = InputPlanner.Plan(_g, Route(states));
        Assert.Null(planned.RefusedReason);
        return planned.Plan!;
    }

    /// <summary>Regenerates the plan the mock-engine script consumes: IKEMENLAB_DUMP_PLAN=path dotnet test --filter DumpMockPlan.</summary>
    [Fact]
    public void DumpMockPlan()
    {
        if (Environment.GetEnvironmentVariable("IKEMENLAB_DUMP_PLAN") is { Length: > 0 } path)
            File.WriteAllText(path, InputPlanner.ToLua(PlanFor("200", "210", "1000")));
    }

    // ------------------------------------------------------------------ planner

    [Fact]
    public void PlannerTurnsACommandIntoPerTickKeys()
    {
        var plan = PlanFor("200", "210");
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(["x"], plan.Steps[0].Input[0].Keys);
        Assert.Equal(["x"], plan.Steps[0].Input[1].Keys);          // held two ticks
        Assert.Empty(plan.Steps[0].Input[^1].Keys);                // then released
        Assert.Null(plan.Steps[0].FromState);
        Assert.Equal(200, plan.Steps[0].ToState);
        Assert.Equal((200, 210, "hit"), (plan.Steps[1].FromState, plan.Steps[1].ToState, plan.Steps[1].Contact));
        Assert.Equal("Cancel", plan.Steps[1].Kind);
    }

    [Fact]
    public void QuarterCircleBecomesDirectionStepsEndingOnTheButton()
    {
        var plan = PlanFor("200", "210", "1000");
        var input = plan.Steps[2].Input.Select(f => string.Join("+", f.Keys)).ToList();
        Assert.Contains("D", input);
        Assert.Contains("D+F", input);
        Assert.Contains("F", input);
        Assert.Contains("x", input);
        Assert.True(input.IndexOf("D") < input.IndexOf("D+F") && input.IndexOf("D+F") < input.IndexOf("F") && input.IndexOf("F") < input.IndexOf("x"));
        Assert.Equal("", input[^1]);
    }

    [Fact]
    public void PlannerRefusesWhatItCannotScript()
    {
        var res = ComboSearch.Find(_g, new ComboOptions { From = "state:200", MaxMoves = 2 });
        if (res.Routes.Count > 0)
            Assert.Contains("neutral-start", InputPlanner.Plan(_g, res.Routes[0]).RefusedReason);

        var withLinks = ComboSearch.Find(_g, new ComboOptions { AllowLinks = true, MaxMoves = 4, Top = 500 });
        var link = withLinks.Routes.FirstOrDefault(r => r.Steps.Any(s => s.Edge.Kind is EdgeKind.Link or EdgeKind.Recovery));
        if (link is not null) Assert.Contains("neutral", InputPlanner.Plan(_g, link).RefusedReason);
    }

    [Fact]
    public void PlanSerialisesToJsonAndToPlainLuaLiterals()
    {
        var plan = PlanFor("200", "210");
        using var doc = JsonDocument.Parse(InputPlanner.ToJson(plan));
        Assert.Equal("ikemenlab.xray.plan/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("steps").GetArrayLength());

        var lua = InputPlanner.ToLua(plan);
        Assert.StartsWith("return {", lua);
        Assert.Contains("toState = 210", lua);
        Assert.Contains("contact = \"hit\"", lua);
        Assert.DoesNotContain("function", lua);           // a plan is data, never code
        Assert.Equal(InputPlanner.ToJson(plan), InputPlanner.ToJson(PlanFor("200", "210")));   // deterministic
    }

    // ------------------------------------------------------------------ verifier, with hand-built traces

    private sealed class TraceBuilder
    {
        private long _f;
        private readonly List<TraceEvent> _events = [new TraceMeta("ikemenlab.xray.trace/0", "test", "0.2", "t", "other", new Dictionary<string, bool>(), ["hook:loop"])];

        private static PlayerSample P(int? state, string? moveType = "I", int? hit = 0, int? contact = 0) =>
            new(state, null, state == 0, "S", moveType, null, null, 1000, 0, 0, 0, null, null, 1, hit, contact, null);

        public TraceBuilder Driver(string kind, string? detail = null) { _events.Add(new DriverEvent(_f, null, kind, null, detail)); return this; }
        public TraceBuilder DriverStep(string kind, int step, string? detail = null) { _events.Add(new DriverEvent(_f, null, kind, step, detail)); return this; }
        public TraceBuilder Input(int step, params string[] keys) { _events.Add(new InputEvent(_f, null, 1, keys, step, "input")); return this; }

        public TraceBuilder Frames(int n, int? p1, int? p2State = 0, string? p2Move = "I", int hit = 0)
        {
            for (var i = 0; i < n; i++)
            {
                _f++;
                _events.Add(new FrameEvent(_f, null, 1, P(p1, p1 == 0 ? "I" : "A", hit, hit), P(p2State, p2Move), 50, null, null, null));
            }

            return this;
        }

        public TraceLog Build(InputPlan plan)
        {
            var meta = ((TraceMeta)_events[0]) with { PlanFingerprint = InputPlanner.Fingerprint(plan) };
            return new TraceLog { Meta = meta, Events = new TraceEvent[] { meta }.Concat(_events.Skip(1))
                .Append(new DriverEvent(_f, null, "plan_complete", null, null)).Append(new EndEvent(_f, null, "planComplete")).ToList(), Issues = [], LineCount = _events.Count + 2 };
        }
    }

    private static TraceBuilder GoodRun(InputPlan plan)
    {
        var b = new TraceBuilder().Driver("plan_start").Frames(3, 0);
        b.Input(1, "x").Frames(2, 0).Input(1).Frames(2, 200).Frames(2, 200, 5000, "H", hit: 1);          // route step 1: 0 -> 200, it connects
        b.Input(2, "y").Frames(2, 210, 5000, "H", hit: 0);                                      // step 2: 200 -> 210 after the hit
        return b;
    }

    [Fact]
    public void VerifiedOnlyWhenEveryTransitionAndTheWholeComboAreObserved()
    {
        var plan = PlanFor("200", "210");
        var b = GoodRun(plan);
        var report = RouteVerifier.Verify(plan, b.Frames(3, 210, 5000, "H").Build(plan));
        Assert.Equal(VerifyStatus.Verified, report.Status);
        Assert.Null(report.Reason);
        Assert.All(report.Steps, s => Assert.Equal(StepOutcome.Observed, s.Outcome));
        Assert.Contains("runtime.contact-observed", report.Steps[1].RuntimeRules);
        Assert.Contains("runtime.opponent-continuous", report.Steps[0].RuntimeRules);
        Assert.True(report.Continuity.Continuous);
        Assert.Equal(Confidence.RuntimeVerified, report.RouteConfidence);
        Assert.Equal(Confidence.StaticProven, _g.Edges.First(e => e.Id == plan.Steps[1].EdgeId).Confidence);   // static confidence untouched
    }

    [Fact]
    public void AComboThatDropsIsFailedWithTheFrameGap()
    {
        var plan = PlanFor("200", "210");
        var b = new TraceBuilder().Driver("plan_start").Frames(3, 0);
        b.Input(1, "x").Frames(2, 200).Frames(2, 200, 5000, "H", hit: 1);
        b.Frames(2, 200, 0, "I", hit: 1);                                                    // P2 recovers while P1 is still in 200
        b.Input(2, "y").Frames(3, 210, 0, "I");
        var report = RouteVerifier.Verify(plan, b.Build(plan));
        Assert.Equal(VerifyStatus.Failed, report.Status);
        Assert.Equal(VerifyReason.ComboDropped, report.Reason);
        Assert.False(report.Continuity.Continuous);
        Assert.True(report.Continuity.DropGapFrames >= 2);
        Assert.Equal(Confidence.Inferred, report.RouteConfidence);
        Assert.All(report.Steps, s => Assert.DoesNotContain("runtime.opponent-continuous", s.RuntimeRules));
    }

    [Fact]
    public void NoContactWrongStateAndMissingTransitionAreNamedSeparately()
    {
        var plan = PlanFor("200", "210");

        var whiff = new TraceBuilder().Driver("plan_start").Frames(2, 0);
        whiff.Input(1, "x").Frames(3, 200).Frames(3, 0);
        var r1 = RouteVerifier.Verify(plan, whiff.Build(plan));
        Assert.Equal((VerifyStatus.Failed, 2, VerifyReason.NoContact), (r1.Status, r1.FailedStep, r1.Reason));

        // Same trace, but the driver says it stopped feeding step 2 because the precondition never held.
        // That happened before the move was ever attempted, so it must outrank NoContact.
        var stopped = new TraceBuilder().Driver("plan_start").Frames(2, 0);
        stopped.Input(1, "x").Frames(3, 200).Frames(3, 0);
        stopped.DriverStep("step_wait", 2, plan.Steps[1].EdgeId).Frames(181, 0);
        stopped.DriverStep("timeout", 2, "precondition never met for " + plan.Steps[1].EdgeId);
        var r1b = RouteVerifier.Verify(plan, stopped.Build(plan));
        Assert.Equal((VerifyStatus.Failed, 2, VerifyReason.PreconditionNeverMet), (r1b.Status, r1b.FailedStep, r1b.Reason));
        Assert.Contains("was not attempted", r1b.Steps[1].Detail);                         // evidence-based wording; the reason code above is unchanged

        var wrong = new TraceBuilder().Driver("plan_start").Frames(2, 0);
        wrong.Input(1, "x").Frames(2, 200, 5000, "H", 1).Input(2, "y").Frames(3, 9999, 5000, "H");
        var r2 = RouteVerifier.Verify(plan, wrong.Build(plan));
        Assert.Equal((VerifyStatus.Failed, VerifyReason.WrongState), (r2.Status, r2.Reason));
        Assert.Contains("9999", r2.Steps[1].Detail);

        var never = new TraceBuilder().Driver("plan_start").Frames(10, 0);
        never.Input(1, "x");
        var r3 = RouteVerifier.Verify(plan, never.Build(plan));
        Assert.Equal(VerifyReason.TelemetryMissing, r3.Reason);
        Assert.Equal(VerifyStatus.Inconclusive, r3.Status);
        Assert.Equal(1, r3.FailedStep);
        Assert.Equal(StepOutcome.NotReached, r3.Steps[1].Outcome);
    }

    [Fact]
    public void WithoutRealInputOrTelemetryTheVerdictIsInconclusiveNeverVerified()
    {
        var plan = PlanFor("200", "210");
        var noInject = new TraceBuilder().Driver("inject_unavailable", "no injector is installed").Frames(5, 0).Build(plan);
        var r1 = RouteVerifier.Verify(plan, noInject);
        Assert.Equal((VerifyStatus.Inconclusive, VerifyReason.InputInjectionUnavailable), (r1.Status, r1.Reason));
        Assert.NotEqual(Confidence.RuntimeVerified, r1.RouteConfidence);

        var empty = RouteVerifier.Verify(plan, new TraceBuilder().Build(plan));
        Assert.Equal((VerifyStatus.Inconclusive, VerifyReason.NoMatchFrames), (empty.Status, empty.Reason));

        // A run whose states were reached but which never reports feeding input is not credited to the driver.
        var noInputEvents = new TraceBuilder().Driver("plan_start").Frames(3, 0).Frames(2, 200, 5000, "H", 1).Frames(2, 210, 5000, "H").Build(plan);
        Assert.Equal(VerifyStatus.Inconclusive, RouteVerifier.Verify(plan, noInputEvents).Status);
    }

    [Fact]
    public void ReportJsonIsVersionedAndDeterministic()
    {
        var plan = PlanFor("200", "210");
        var report = RouteVerifier.Verify(plan, GoodRun(plan).Frames(3, 210, 5000, "H").Build(plan));
        var json = RouteVerifier.ToJson(report);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ikemenlab.xray.verify/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("Verified", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("RuntimeVerified", doc.RootElement.GetProperty("routeConfidence").GetString());
        Assert.Equal(json, RouteVerifier.ToJson(report));
    }

    // ------------------------------------------------------------------ traces produced by the real driver against a mock engine (scripts/xray_driver_mock.py)

    private static TraceLog BindLegacyFixture(TraceLog log, InputPlan plan) => new()
    {
        Meta = log.Meta! with { PlanFingerprint = InputPlanner.Fingerprint(plan) }, Events = log.Events,
        Issues = log.Issues, LineCount = log.LineCount
    };

    private InputPlan MockPlan => PlanFor("200", "210", "1000");

    [Theory]
    [InlineData("verified", VerifyStatus.Verified, null)]
    [InlineData("dropped", VerifyStatus.Failed, VerifyReason.ComboDropped)]
    [InlineData("nocontact", VerifyStatus.Failed, VerifyReason.NoContact)]
    [InlineData("driverstop", VerifyStatus.Failed, VerifyReason.PreconditionNeverMet)]
    [InlineData("wrongstate", VerifyStatus.Failed, VerifyReason.WrongState)]
    [InlineData("noinject", VerifyStatus.Inconclusive, VerifyReason.InputInjectionUnavailable)]
    public void TracesFromTheLuaDriverAreJudgedAsExpected(string scenario, VerifyStatus status, string? reason)
    {
        var log = TraceReader.ReadFile(Path.Combine(FixtureDir, $"xray_driver_mock_{scenario}.jsonl"));
        Assert.Empty(log.Issues);
        var report = RouteVerifier.Verify(MockPlan, BindLegacyFixture(log, MockPlan));
        Assert.Equal((status, reason), (report.Status, report.Reason));
        if (scenario == "verified") Assert.Contains(log.Events.OfType<InputEvent>(), e => e.Keys.SequenceEqual(["x"]));
        if (scenario == "noinject") Assert.DoesNotContain(log.Events.OfType<InputEvent>(), e => e.Keys.Count > 0);
    }

    // ------------------------------------------------------------------ runner orchestration

    private sealed class FakeRunner(string traceText) : IEngineRunner
    {
        public RuntimeSandbox? Seen;
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout)
        {
            Seen = sandbox;
            File.WriteAllText(sandbox.TracePath, traceText);
            return new EngineRunResult(0, false, null);
        }
    }

    private string FakeInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "xray-vr-" + Guid.NewGuid().ToString("N"));
        _cleanup.Add(root);
        void W(string rel, string text)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("Ikemen_GO.exe", "exe");
        W("data/select.def", "[Characters]\nkfm\n");
        W("external/script/main.lua", "main()\n");
        W("chars/ComboGuy/ComboGuy.def", "[Info]\n");
        W("chars/kfm/kfm.def", "[Info]\n");
        W("stages/ring.def", "[StageInfo]\n");
        return root;
    }

    [Fact]
    public void RunnerBuildsAVerifySandboxRunsItAndJudgesTheTrace()
    {
        var source = FakeInstall();
        var baseDir = Path.Combine(Path.GetTempPath(), "xray-vb-" + Guid.NewGuid().ToString("N"));
        _cleanup.Add(baseDir);
        var trace = File.ReadAllText(Path.Combine(FixtureDir, "xray_driver_mock_verified.jsonl"));
        trace = trace.Replace("\"type\":\"meta\"", "\"type\":\"meta\",\"planFingerprint\":\"" + InputPlanner.Fingerprint(MockPlan) + "\"");
        var runner = new FakeRunner(trace);
        var sandboxRequest = new SandboxRequest(source, "ComboGuy", "ComboGuy/ComboGuy.def", "kfm", "kfm/kfm.def", "stages/ring.def", 1500, baseDir);
        var result = VerifyRunner.Run(_g, Route("200", "210", "1000"), new VerifyRunRequest(sandboxRequest, TimeSpan.FromSeconds(5), KeepSandbox: true), runner);

        Assert.Equal(VerifyStatus.Verified, result.Report.Status);
        var root = result.SandboxPath!;
        var mods = Path.Combine(root, "external", "mods");
        Assert.True(File.Exists(Path.Combine(mods, "xray_plan.lua")));
        Assert.True(File.Exists(Path.Combine(mods, "xray_driver.lua")));
        Assert.Contains("inject", File.ReadAllText(Path.Combine(mods, "xray_inject.lua")));
        Assert.Contains("plan = \"external/mods/xray_plan.lua\"", File.ReadAllText(Path.Combine(mods, "xray_config.lua")));
        Assert.DoesNotContain("-p1.ai", runner.Seen!.Arguments);                  // nobody is on the engine AI in verify mode
        Assert.Contains(result.Report.Notes, n => n.Contains("No input adapter"));
        Assert.True(RuntimeSandbox.Delete(root));
        Assert.False(File.Exists(Path.Combine(source, "external", "mods", "xray_plan.lua")));   // the source install is never written
    }

    [Fact]
    public void RunnerRemovesTheSandboxUnlessAskedToKeepIt()
    {
        var source = FakeInstall();
        var baseDir = Path.Combine(Path.GetTempPath(), "xray-vb-" + Guid.NewGuid().ToString("N"));
        _cleanup.Add(baseDir);
        var runner = new FakeRunner("");
        var req = new SandboxRequest(source, "ComboGuy", "ComboGuy/ComboGuy.def", "kfm", "kfm/kfm.def", "stages/ring.def", 10, baseDir);
        var result = VerifyRunner.Run(_g, Route("200", "210"), new VerifyRunRequest(req, TimeSpan.FromSeconds(1)), runner);
        Assert.Null(result.SandboxPath);
        Assert.False(Directory.Exists(runner.Seen!.Root));
        Assert.Equal(VerifyStatus.Inconclusive, result.Report.Status);
    }

    // ------------------------------------------------------------------ ranker

    [Fact]
    public void RankerPrefersTheCleanCharacterAndExplainsWhy()
    {
        var clean = SubjectRanker.Score(_g);
        var hostile = SubjectRanker.Score(CandidateGraph.Build(CharacterSemanticIndexer.Build(_fx.Root, _fx.Valentine)));
        Assert.True(clean.Score > hostile.Score, $"{clean.Score} vs {hostile.Score}");
        Assert.True(clean.ScriptableRoutes > 0);
        Assert.NotNull(clean.BestRoute);
        Assert.NotEmpty(clean.Reasons);
    }

    // ------------------------------------------------------------------ rules and CLI

    [Fact]
    public void OnlyRuntimePrefixedRulesMayClaimRuntimeVerified()
    {
        var runtime = EvidenceRules.Rules.Where(r => r.Confidence == Confidence.RuntimeVerified).ToList();
        Assert.Equal(3, runtime.Count);
        Assert.All(runtime, r => Assert.StartsWith("runtime.", r.Id));
        Assert.DoesNotContain(EvidenceRules.Rules, r => r.Confidence != Confidence.RuntimeVerified && r.Id.StartsWith("runtime.", StringComparison.Ordinal));
        // Every rule a verdict can cite is registered.
        var plan = PlanFor("200", "210");
        var report = RouteVerifier.Verify(plan, GoodRun(plan).Frames(3, 210, 5000, "H").Build(plan));
        foreach (var id in report.Steps.SelectMany(s => s.RuntimeRules)) Assert.Contains(EvidenceRules.Rules, r => r.Id == id);
    }

    private (int Code, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = CliApp.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void CliPlansAndJudgesATraceWithDistinctExitCodes()
    {
        var target = Path.Combine(_fx.Root, "chars", "ComboGuy");
        var plan = Cli("xray", "verify-plan", target, "--root", _fx.Root, "--max-moves", "3");
        Assert.Equal(0, plan.Code);
        using (var doc = JsonDocument.Parse(plan.Out)) Assert.Equal("ikemenlab.xray.plan/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.StartsWith("return {", Cli("xray", "verify-plan", target, "--root", _fx.Root, "--max-moves", "3", "--lua").Out);

        var verified = Cli("xray", "verify-trace", target, "--root", _fx.Root, "--max-moves", "3", "--route", "1", "--trace", Path.Combine(FixtureDir, "xray_driver_mock_noinject.jsonl"));
        Assert.Equal(5, verified.Code);                                   // inconclusive: could not test
        Assert.Contains("InputInjectionUnavailable", verified.Out);
        Assert.Equal(2, Cli("xray", "verify-trace", target).Code == 0 ? 0 : 2);
        Assert.Equal(3, Cli("xray", "verify-trace", target, "--root", _fx.Root, "--trace", "missing.jsonl").Code);
    }

    [Fact]
    public void CliRanksTheInstalledCharacters()
    {
        var r = Cli("xray", "rank-subjects", "--root", _fx.Root);
        Assert.Equal(0, r.Code);
        using var doc = JsonDocument.Parse(r.Out);
        var ranking = doc.RootElement.GetProperty("ranking");
        Assert.True(ranking.GetArrayLength() >= 2);
        Assert.Equal("ComboGuy", ranking[0].GetProperty("character").GetString());
    }
}
