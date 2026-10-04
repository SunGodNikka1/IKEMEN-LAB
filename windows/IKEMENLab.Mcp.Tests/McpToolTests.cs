using System.Security.Cryptography;
using System.Text.Json.Nodes;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Mcp.Tests;

/// <summary>
/// Phase 4: the Observe and Experiment tools on the ComboGuy fixture. Runs go through the real Core planner, sandbox, driver plan, playback session,
/// verifiers and stores; only the engine process is replaced by <see cref="TestEngine"/>.
/// </summary>
public class McpToolTests
{
    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) o[k] = v?.DeepClone();   // nodes from earlier answers (or shared constants) can be passed as they are
        return o;
    }

    private static readonly (string, JsonNode?) Combo = ("character", "ComboGuy");

    // ------------------------------------------------------------------ Observe

    [Fact]
    public void InspectCharacterIsASmallSemanticOverview()
    {
        using var h = new McpHarness();
        var r = h.Call("inspect_character", Args(Combo));
        Assert.Equal("Combo Guy", r["character"]!["name"]!.GetValue<string>());
        Assert.Equal("ComboGuy", r["character"]!["folder"]!.GetValue<string>());
        Assert.True(r["abilities"]!["total"]!.GetValue<int>() >= 3);
        Assert.True(r["playback"]!["ready"]!.GetValue<bool>());
        var useful = r["usefulMoves"]!.AsArray();
        Assert.NotEmpty(useful);
        Assert.All(useful, m => Assert.StartsWith("ability:", m!["id"]!.GetValue<string>()));
        Assert.Contains("Static reading", r["basis"]!.GetValue<string>());
        Assert.True(r.ToJsonString().Length < 6_000, "inspect_character should stay a small slice.");
        Assert.DoesNotContain("[Statedef", r.ToJsonString());                     // no source text in an overview

        var list = h.Call("list_abilities", Args(Combo, ("limit", 2)));
        Assert.Equal(2, list["returned"]!.GetValue<int>());
        Assert.Equal(2, list["nextOffset"]!.GetValue<int>());
        var normal = h.Call("list_abilities", Args(Combo, ("category", "normal")));
        Assert.All(normal["abilities"]!.AsArray(), a => Assert.Equal("Normal", a!["category"]!.GetValue<string>()));
        h.Error("list_abilities", Args(Combo, ("category", "spells")));

        var ability = h.Call("inspect_ability", Args(Combo, ("ability", "200")));
        Assert.Equal("ability:200", ability["ability"]!["id"]!.GetValue<string>());
        Assert.True(ability["playAbility"]!["available"]!.GetValue<bool>());
        Assert.Equal("x", ability["playAbility"]!["command"]!.GetValue<string>());
        Assert.NotEmpty(ability["followUps"]!.AsArray());

        var state = h.Call("explain_state", Args(Combo, ("state", "200")));
        Assert.Equal(200, state["state"]!["state"]!.GetValue<int>());
        Assert.NotEmpty(state["enteredBy"]!.AsArray());

        var unknown = h.Error("inspect_character", Args(("character", "NoSuchFighter")));
        Assert.Contains("NoSuchFighter", unknown["error"]!.GetValue<string>());
    }

    [Fact]
    public void ARenamedAbilityShowsItsNameEverywhereAndCanBeAskedForByName()
    {
        using var h = new McpHarness();
        Assert.Equal("ability:200", h.Call("inspect_ability", Args(Combo, ("ability", "200")))["ability"]!["id"]!.GetValue<string>());

        // The user renames it in the app while the server runs (same names store): the next call shows it, no restart.
        var c = h.App.Context.Load("ComboGuy");
        Assert.Null(c.Names.Rename("ability:200", "Kung Fu Palm"));
        Assert.Null(c.Names.Rename("ability:210", "Normal A"));

        var list = h.Call("list_abilities", Args(Combo));
        var row = list["abilities"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "ability:200")!;
        Assert.Equal("Kung Fu Palm", row["name"]!.GetValue<string>());
        Assert.NotEqual("Kung Fu Palm", row["xrayName"]!.GetValue<string>());       // X-Ray's own name stays available, secondary

        var byName = h.Call("inspect_ability", Args(Combo, ("ability", "kung fu palm")));
        Assert.Equal("ability:200", byName["ability"]!["id"]!.GetValue<string>());
        Assert.Contains(byName["followUps"]!.AsArray(), f => f!["to"]!["name"]!.GetValue<string>() == "Normal A");
        Assert.Equal("Kung Fu Palm", h.Call("explain_state", Args(Combo, ("state", "200")))["state"]!["name"]!.GetValue<string>());

        var plan = h.Call("test_sequence", Args(Combo, ("plan_only", true), ("steps", new JsonArray(
            new JsonObject { ["action"] = "ability", ["ability"] = "Kung Fu Palm" }, new JsonObject { ["action"] = "ability", ["ability"] = "Normal A" }))));
        Assert.Equal("Kung Fu Palm → Normal A", plan["steps"]!.GetValue<string>());
        Assert.Equal("ability:200 > ability:210", plan["spec"]!.GetValue<string>());
        Assert.True(plan["ready"]!.GetValue<bool>());

        var job = h.Call("play_ability", Args(Combo, ("ability", "Kung Fu Palm"), ("wait_seconds", 20)));
        Assert.Equal("completed", McpHarness.Status(job));
        Assert.Equal("Kung Fu Palm", job["result"]!["ability"]!["name"]!.GetValue<string>());
        Assert.Contains("Kung Fu Palm", job["result"]!["headline"]!.GetValue<string>());
    }

    [Fact]
    public void GetSourceNeedsAnObjectOrARangeAndNeverReturnsAWholeFile()
    {
        using var h = new McpHarness();
        h.Error("get_source", Args(Combo));                                           // nothing named: refused
        var noStart = h.Error("get_source", Args(Combo, ("file", "ComboGuy.cns")));
        Assert.Contains("start_line", noStart["error"]!.GetValue<string>());
        h.Error("get_source", Args(Combo, ("file", "../../Windows/win.ini"), ("start_line", 1)));
        h.Error("get_source", Args(Combo, ("file", "C:/Windows/win.ini"), ("start_line", 1)));
        h.Error("get_source", Args(Combo, ("object", "state:200"), ("file", "ComboGuy.cns"), ("start_line", 1)));
        var tooMany = h.Error("get_source", Args(Combo, ("object", "state:200"), ("max_lines", 500)));
        Assert.Contains("1 to 200", tooMany["error"]!.GetValue<string>());

        var state = h.Call("get_source", Args(Combo, ("object", "200")));
        Assert.Equal(200, state["object"]!["state"]!.GetValue<int>());
        Assert.Contains("[Statedef 200", state["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[State 200", state["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);   // its controllers come with it
        var fileLines = state["fileLines"]!.GetValue<int>();

        var cut = h.Call("get_source", Args(Combo, ("object", "state:200"), ("max_lines", 3)));
        Assert.True(cut["truncated"]!.GetValue<bool>());
        Assert.Equal(3, cut["text"]!.GetValue<string>().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.True(cut["nextStartLine"]!.GetValue<int>() > 1);

        var range = h.Call("get_source", Args(Combo, ("file", "ComboGuy.cns"), ("start_line", 1), ("end_line", 100_000)));
        Assert.True(range["text"]!.GetValue<string>().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= Lab.ObserveTools.DefaultSourceLines);
        if (fileLines > Lab.ObserveTools.DefaultSourceLines) Assert.True(range["truncated"]!.GetValue<bool>());
        Assert.True(range["text"]!.GetValue<string>().Length <= Lab.ObserveTools.MaxSourceChars);
    }

    // ------------------------------------------------------------------ Experiment: Play Ability / Preview

    [Fact]
    public void PlayAbilityRunsThroughTheExistingPlaybackAndKeepsItsRecordOutOfTheUsersHistory()
    {
        using var h = new McpHarness();
        var started = h.Call("play_ability", Args(Combo, ("ability", "200")));
        Assert.StartsWith("job-", started["jobId"]!.GetValue<string>());
        Assert.Contains(McpHarness.Status(started), new[] { "queued", "running", "completed" });

        var job = h.Finish(started["jobId"]!.GetValue<string>());
        Assert.Equal("completed", McpHarness.Status(job));
        var r = job["result"]!;
        Assert.Equal(("Performed", "Performed"), (r["status"]!.GetValue<string>(), r["statusCode"]!.GetValue<string>()));
        Assert.Contains("runtime.transition-observed", r["evidence"]!.GetValue<string>());
        Assert.Equal("yes", r["observed"]!["connected"]!.GetValue<string>());
        Assert.NotNull(r["observed"]!["situation"]!["text"]);
        Assert.Equal(1, h.Engine.Runs);

        var runId = r["runId"]!.GetValue<string>();
        Assert.True(File.Exists(Path.Combine(h.DataDir, "xray-mcp-runs", runId, "ability.json")));
        Assert.False(Directory.Exists(Path.Combine(h.DataDir, "xray-playback")));    // the user's Play Combo / Ability history is untouched
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.Sandboxes));             // the sandbox was deleted

        var trace = h.Call("get_runtime_trace", Args(("run_id", runId), Combo));
        Assert.Equal("ability", trace["run"]!["mode"]!.GetValue<string>());
        Assert.Contains(trace["events"]!.AsArray(), e => e!["event"]!.GetValue<string>().StartsWith("Holding x", StringComparison.Ordinal));
        var sampled = h.Call("get_runtime_trace", Args(("run_id", runId), ("from_frame", 5), ("to_frame", 20), ("max_samples", 4)));
        Assert.True(sampled["samples"]!.AsArray().Count <= 4);
        h.Error("get_runtime_trace", Args(("run_id", "../../etc")));

        // An ability entered only from another move (the super, from QCF x on hit) is refused at once, with the reason; no job, no engine.
        var refused = h.Call("play_ability", Args(Combo, ("ability", "3000")));
        Assert.False(refused["started"]!.GetValue<bool>());
        Assert.NotNull(refused["why"]);
        Assert.Equal(1, h.Engine.Runs);
    }

    [Fact]
    public void PreviewStateIsAlwaysProofFalseAndNeverAVerdict()
    {
        using var h = new McpHarness();
        var job = h.Call("preview_state", Args(Combo, ("state", "210"), ("wait_seconds", 20)));
        Assert.Equal("completed", McpHarness.Status(job));
        var r = job["result"]!;
        Assert.False(r["proof"]!.GetValue<bool>());
        Assert.Equal(PreviewReport.NotProof, r["notProof"]!.GetValue<string>());
        Assert.Equal("Forced", r["statusCode"]!.GetValue<string>());
        Assert.Contains("not proof", r["status"]!.GetValue<string>());
        var text = r.ToJsonString();
        Assert.DoesNotContain("Performed", text);
        Assert.DoesNotContain("runtime.transition-observed", text);
        Assert.Null(r["evidence"]);
        var trace = h.Call("get_runtime_trace", Args(("run_id", r["runId"]!.GetValue<string>())));
        Assert.False(trace["run"]!["proof"]!.GetValue<bool>());
        Assert.Contains("\"proof\": false", File.ReadAllText(Path.Combine(h.DataDir, "xray-mcp-runs", r["runId"]!.GetValue<string>(), "meta.json")));
    }

    // ------------------------------------------------------------------ Experiment: sequences

    [Fact]
    public void ASequenceThroughMcpGetsExactlyTheVerdictCoreGivesTheSameTrace()
    {
        using var h = new McpHarness();
        foreach (var (scenario, spec, verdict, text) in new[]
                 {
                     ("seq_true", "200 > 210", SequenceVerdict.TrueCombo, "True combo"),
                     ("seq_gap", "200 > walk:5 > 220", SequenceVerdict.ConnectedSequence, "Connected sequence"),
                     ("seq_far", "200 > back:10 > 220", SequenceVerdict.DidNotConnect, "Did not connect")
                 })
        {
            h.Engine.Scenario = scenario;
            var job = h.Call("test_sequence", Args(Combo, ("spec", spec), ("wait_seconds", 30)));
            Assert.Equal("completed", McpHarness.Status(job));
            var run = job["result"]!["run"]!;
            Assert.Equal((text, verdict.ToString()), (run["verdict"]!.GetValue<string>(), run["verdictCode"]!.GetValue<string>()));

            // Core, directly: the same planner and the same verifier on the very trace the trial recorded.
            var c = h.App.Context.Load("ComboGuy");
            var expId = job["result"]!["experimentId"]!.GetValue<string>();
            var exp = h.App.Context.Experiments.List().Single(e => e.Id == expId);
            Assert.Equal(ExperimentSummary.McpOrigin, exp.Origin);
            var trialDir = Path.Combine(exp.Directory, "trials", exp.Trials.Single().RecordId);
            var setup = h.App.Context.Setup(c);
            var sequence = new Sequence(exp.Scope.SequenceId, exp.Scope.SequenceName, 1, SequenceSpec.Parse(spec));
            var (prepared, _) = SequenceExperimentRunner.Prepare(c.Graph, sequence, c.Root, c.SubjectFolder, c.SubjectDef, setup, 1);
            Assert.Equal(exp.Scope.PlanFingerprint, InputPlanner.Fingerprint(prepared!.Request.Plan));
            var core = SequenceVerifier.Verify(prepared.Request.Plan, TraceReader.ReadFile(Path.Combine(trialDir, "trace.jsonl")), prepared.Request.Labels);
            Assert.Equal(core.Verdict, verdict);
            Assert.Equal(core.Summary, run["summary"]!.GetValue<string>());
            Assert.Equal(core.Steps.Select(s => s.Outcome.ToString()), run["steps"]!.AsArray().Select(s => s!["outcome"]!.GetValue<string>()));
            if (verdict == SequenceVerdict.ConnectedSequence)
            {
                Assert.True(run["opponentOutOfHitstunFrames"]!.GetValue<int>() > 0);   // connected, but never called a true combo
                Assert.DoesNotContain("True combo", run["summary"]!.GetValue<string>() + job["result"]!["headline"]!.GetValue<string>());
            }

            var stored = h.Call("get_experiment_results", Args(("experiment_id", expId)));
            Assert.Equal(text, stored["lastTrial"]!["verdict"]!.GetValue<string>());
            Assert.Equal(core.Summary, stored["headline"]!.GetValue<string>());
        }

        // Typed steps and the compact spec are the same sequence (same id, same plan).
        var typed = h.Call("test_sequence", Args(Combo, ("plan_only", true), ("steps", new JsonArray(
            new JsonObject { ["action"] = "ability", ["ability"] = "ability:200" }, new JsonObject { ["action"] = "walk_forward", ["frames"] = 5 }, "220"))));
        var spec2 = h.Call("test_sequence", Args(Combo, ("plan_only", true), ("spec", "200 > walk:5 > 220")));
        Assert.Equal(spec2["sequenceId"]!.GetValue<string>(), typed["sequenceId"]!.GetValue<string>());
        Assert.Equal("Wait 20f", h.Call("test_sequence", Args(Combo, ("plan_only", true), ("steps", new JsonArray("200", "wait:20", "210"))))["plan"]![1]!["label"]!.GetValue<string>());

        // Every typed action builds.
        var all = h.Call("test_sequence", Args(Combo, ("plan_only", true), ("steps", new JsonArray(
            new JsonObject { ["action"] = "ability", ["ability"] = "200" }, new JsonObject { ["action"] = "walk_backward", ["frames"] = 3 },
            new JsonObject { ["action"] = "wait", ["frames"] = 4 }, new JsonObject { ["action"] = "chase", ["distance"] = 35, ["stop_if_opponent_recovers"] = true },
            new JsonObject { ["action"] = "dash" }, new JsonObject { ["action"] = "jump" }))));
        Assert.Equal("ability:200 > back:3 > wait:4 > chase:35! > dash > jump", all["spec"]!.GetValue<string>());
        h.Error("test_sequence", Args(Combo, ("steps", new JsonArray(new JsonObject { ["action"] = "teleport" }))));
        h.Error("test_sequence", Args(Combo, ("steps", new JsonArray(new JsonObject { ["action"] = "wait", ["frames"] = 9000 }))));
    }

    [Fact]
    public void SavedVariantsRunAsExperimentsAndCompareSideBySide()
    {
        using var h = new McpHarness();
        var a = h.Call("create_experiment", Args(Combo, ("name", "Jab into strong"), ("spec", "200 > 210")));
        var b = h.Call("create_experiment", Args(Combo, ("name", "Jab, wait 8, strong"), ("spec", "200 > wait:8 > 210")));
        Assert.True(a["ready"]!.GetValue<bool>());
        Assert.Equal(1, a["version"]!.GetValue<int>());
        Assert.Equal(0, h.Engine.Runs);                                               // creating launches nothing

        // The variants are the Sequence Lab's own saved variants.
        var saved = h.App.Context.Sequences.Load(h.App.Context.Load("ComboGuy").Folder).Sequences;
        Assert.Equal(2, saved.Count);

        h.Engine.Scenario = "seq_true";
        var runA = h.Call("run_experiment", Args(Combo, ("sequence_id", a["sequenceId"]!), ("trials", 2), ("wait_seconds", 30)));
        h.Engine.Scenario = "seq_wait";
        var runB = h.Call("run_experiment", Args(Combo, ("sequence_id", b["sequenceId"]!), ("wait_seconds", 30)));
        Assert.Equal(("completed", "completed"), (McpHarness.Status(runA), McpHarness.Status(runB)));
        Assert.Equal(2, runA["result"]!["stats"]!["completed"]!.GetValue<int>());
        Assert.Equal("100%", runA["result"]!["stats"]!["successRate"]!.GetValue<string>());

        var cmp = h.Call("compare_experiments", Args(("experiment_a", runA["result"]!["experimentId"]!), ("experiment_b", runB["result"]!["experimentId"]!)));
        Assert.True(cmp["sameSetup"]!.GetValue<bool>());
        Assert.Null(cmp["warning"]);
        var rows = cmp["rows"]!.AsArray();
        Assert.Contains(rows, r => r!["metric"]!.GetValue<string>() == "True combos");
        Assert.Equal("Jab into strong v1", rows.Single(r => r!["metric"]!.GetValue<string>() == "Sequence")!["a"]!.GetValue<string>());

        // A different spacing is a different setup: the comparison says so.
        h.Engine.Scenario = "seq_true";
        var runC = h.Call("run_experiment", Args(Combo, ("sequence_id", a["sequenceId"]!), ("setup", new JsonObject { ["approach_distance"] = 90 }), ("wait_seconds", 30)));
        var cmp2 = h.Call("compare_experiments", Args(("experiment_a", runA["result"]!["experimentId"]!), ("experiment_b", runC["result"]!["experimentId"]!)));
        Assert.False(cmp2["sameSetup"]!.GetValue<bool>());
        Assert.Contains("not directly comparable", cmp2["warning"]!.GetValue<string>());

        // Editing a variant's steps saves a new version; results keep naming the version they ran.
        var edited = h.Call("create_experiment", Args(Combo, ("name", "Jab into strong"), ("sequence_id", a["sequenceId"]!), ("spec", "200 > wait:2 > 210")));
        Assert.Equal(2, edited["version"]!.GetValue<int>());
        var listed = h.Call("list_experiments", Args(Combo));
        Assert.Equal(3, listed["experiments"]!.AsArray().Count);
        Assert.Contains(listed["experiments"]!.AsArray(), e => e!["sequence"]!["version"]!.GetValue<int>() == 1 && e!["sequence"]!["name"]!.GetValue<string>() == "Jab into strong");

        // ×50 needs consent; without it nothing starts.
        var costly = h.Call("run_experiment", Args(Combo, ("sequence_id", a["sequenceId"]!), ("trials", 50)));
        Assert.False(costly["started"]!.GetValue<bool>());
        Assert.Contains("×50 launches IKEMEN 50 time(s)", costly["cost"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ jobs

    [Fact]
    public void ALongJobIsQueuedRunsWithProgressAndCompletes()
    {
        using var h = new McpHarness();
        h.Engine.Block = true;
        var first = h.Call("test_sequence", Args(Combo, ("spec", "200 > 210"), ("trials", 3)));
        Assert.True(h.Engine.Entered.Wait(TimeSpan.FromSeconds(20)));
        var second = h.Call("play_ability", Args(Combo, ("ability", "200")));
        Assert.Equal("queued", McpHarness.Status(second));
        Assert.Equal(1, second["queuedBehind"]!.GetValue<int>());

        var running = h.Call("get_job", Args(("job_id", first["jobId"]!)));
        Assert.Equal("running", McpHarness.Status(running));
        Assert.Equal((1, 3), (running["progress"]!["trial"]!.GetValue<int>(), running["progress"]!["of"]!.GetValue<int>()));
        Assert.Contains("Trial 1 of 3", running["phase"]!.GetValue<string>());
        Assert.NotNull(running["next"]);

        var recent = h.Call("get_job", new JsonObject());
        Assert.Equal(2, recent["jobs"]!.AsArray().Count);

        h.Engine.Block = false;
        h.Engine.Release();
        var done = h.Finish(first["jobId"]!.GetValue<string>());
        Assert.Equal("completed", McpHarness.Status(done));
        Assert.Equal(3, done["result"]!["stats"]!["completed"]!.GetValue<int>());
        Assert.Null(done["next"]);
        Assert.Equal("completed", McpHarness.Status(h.Finish(second["jobId"]!.GetValue<string>())));
        Assert.Equal(4, h.Engine.Runs);
    }

    [Fact]
    public void CancelStopsTheEngineDeletesTheSandboxAndCommitsNothing()
    {
        using var h = new McpHarness();
        h.Engine.Block = true;
        var job = h.Call("play_ability", Args(Combo, ("ability", "200")));
        var queued = h.Call("play_ability", Args(Combo, ("ability", "200")));
        Assert.True(h.Engine.Entered.Wait(TimeSpan.FromSeconds(20)));

        var cq = h.Call("cancel_job", Args(("job_id", queued["jobId"]!)));
        Assert.True(cq["accepted"]!.GetValue<bool>());
        Assert.Equal("cancelled", cq["status"]!.GetValue<string>());

        var c = h.Call("cancel_job", Args(("job_id", job["jobId"]!)));
        Assert.True(c["accepted"]!.GetValue<bool>());
        var end = h.Finish(job["jobId"]!.GetValue<string>());
        Assert.Equal("cancelled", McpHarness.Status(end));
        Assert.Null(end["result"]);
        Assert.Equal(1, h.Engine.Runs);                                               // the queued one never launched
        Assert.False(Directory.Exists(Path.Combine(h.DataDir, "xray-mcp-runs")) && Directory.EnumerateDirectories(Path.Combine(h.DataDir, "xray-mcp-runs")).Any());
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.Sandboxes));
        Assert.False(h.Call("cancel_job", Args(("job_id", job["jobId"]!)))["accepted"]!.GetValue<bool>());   // already over

        // A multi-trial experiment cancelled after its first trial keeps that trial and says it was stopped.
        h.Engine.BlockFromRun = 3;                                                    // run 2 = trial 1 finishes; run 3 = trial 2 is held
        var exp = h.Call("test_sequence", Args(Combo, ("spec", "200 > 210"), ("trials", 3)));
        Assert.True(h.Engine.Entered.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(h.Engine.Entered.Wait(TimeSpan.FromSeconds(20)));                  // trial 2 is running (held)
        Assert.True(h.Call("cancel_job", Args(("job_id", exp["jobId"]!)))["accepted"]!.GetValue<bool>());
        var stopped = h.Finish(exp["jobId"]!.GetValue<string>());
        Assert.Equal("cancelled", McpHarness.Status(stopped));
        Assert.Equal(1, stopped["result"]!["stats"]!["completed"]!.GetValue<int>());
        Assert.True(stopped["result"]!["stats"]!["stopped"]!.GetValue<bool>());
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.Sandboxes));
    }

    [Fact]
    public async Task ACancelBetweenTheJobStartingAndTheSessionGoingBusyStillStopsItBeforeAnyLaunch()
    {
        using var h = new McpHarness();
        using var handedOver = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var launched = false;
        var setup = new PlaybackSetup(h.Root, "IkemenGuy", "IkemenGuy/IkemenGuy.def", "stages/ring.def", h.EnginePath, null, true, [], []);
        var scope = new ExperimentScope("char:ComboGuy", "H", null, "IkemenGuy", "stages/ring.def", 60, "seq-x", 1, "x", "FP");
        var job = h.App.Jobs.Enqueue("sequence", "race", null, async session =>
        {
            handedOver.Set();
            proceed.Wait(TimeSpan.FromSeconds(10));                                   // the cancel arrives here: started, but the session is not busy yet
            await session.RunSequenceAsync(new SequenceJob("seq-x@v1", "x", setup, 1, "x"), (gate, _) =>
            {
                gate.Token.ThrowIfCancellationRequested();
                launched = true;
                return new ExperimentOutcome(new ExperimentSummary("e", DateTime.UtcNow, scope, "x", 1, 1, false, []), null);
            });
            return new Lab.JobOutcome(session.State == PlaybackState.Cancelled ? Lab.JobStatus.Cancelled : Lab.JobStatus.Completed, null, null);
        });

        Assert.True(handedOver.Wait(TimeSpan.FromSeconds(10)));
        var (accepted, message) = h.App.Jobs.Cancel(job.Id);
        Assert.True(accepted, message);
        Assert.Contains("before the engine starts", message);
        proceed.Set();
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Lab.JobStatus.Cancelled, job.Status);
        Assert.False(launched);
    }

    [Fact]
    public async Task ClosingTheServerCancelsARunningJobAndCleansUp()
    {
        var h = new McpHarness();
        try
        {
            h.Engine.Block = true;
            var job = h.Call("play_ability", Args(Combo, ("ability", "200")));
            Assert.True(h.Engine.Entered.Wait(TimeSpan.FromSeconds(20)));
            var shutdown = await h.App.ShutdownAsync(TimeSpan.FromSeconds(20));
            Assert.True(shutdown.Clean, shutdown.Problem);
            Assert.Equal(Lab.JobStatus.Cancelled, h.App.Jobs.Get(job["jobId"]!.GetValue<string>())!.Status);
            Assert.Empty(Directory.EnumerateFileSystemEntries(h.Sandboxes));
            Assert.Throws<InvalidOperationException>(() => h.App.Jobs.Enqueue("x", "x", null, _ => Task.FromResult(new Lab.JobOutcome(Lab.JobStatus.Completed, null, null))));
        }
        finally { h.Dispose(); }
    }

    [Fact]
    public void WhileAnotherClientHoldsTheEngineAJobWaitsAndSaysWhoHasIt()
    {
        using var h = new McpHarness(withBroker: true);
        var app = h.Broker!.Acquire(new RuntimeHolder("IKEMEN Lab app", "Play Combo · x > y", 4242, DateTime.UtcNow));
        var job = h.Call("play_ability", Args(Combo, ("ability", "200")));
        var id = job["jobId"]!.GetValue<string>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        JsonObject state;
        do { state = h.Call("get_job", Args(("job_id", id), ("wait_seconds", 1))); }
        while (state["waitingForEngine"] is null && DateTime.UtcNow < deadline);
        Assert.Equal("queued", McpHarness.Status(state));
        Assert.Contains("IKEMEN Lab app — Play Combo · x > y", state["waitingForEngine"]!.GetValue<string>());
        Assert.Equal(0, h.Engine.Runs);                                               // never raced the other client

        app.Dispose();
        Assert.Equal("completed", McpHarness.Status(h.Finish(id)));
        Assert.Equal(1, h.Engine.Runs);
        Assert.Null(h.Broker.CurrentHolder());                                        // released after the job

        // A queued job that waits too long fails with the reason instead of hanging.
        using var h2 = new McpHarness(withBroker: true, queueTimeout: TimeSpan.FromMilliseconds(300));
        using var hold = h2.Broker!.Acquire(RuntimeHolder.Now("ikemenlab CLI", "runtime-sequence"));
        var late = h2.Finish(h2.Call("play_ability", Args(Combo, ("ability", "200")))["jobId"]!.GetValue<string>());
        Assert.Equal("failed", McpHarness.Status(late));
        Assert.Contains("ikemenlab CLI", late["error"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ data isolation

    [Fact]
    public void McpWritesOnlyUnderItsOwnDataFolderAndNeverTheUsersStoresOrSettings()
    {
        var real = AppDataPaths.GetAppDataDirectory();
        var watched = new[] { "xray-playback", "xray-experiments", Path.Combine("xray", "sequences"), Path.Combine("xray", "names"), "settings.json", "xray-mcp-runs" }
            .Select(p => Path.Combine(real, p)).ToList();
        var before = Snapshot(watched);

        using (var h = new McpHarness())
        {
            var ctx = h.App.Context;
            foreach (var path in new[] { ctx.NameStore.Directory, ctx.Sequences.Directory, ctx.Experiments.Root, ctx.Runs.StoreRoot, ctx.AppPlaybackRoot, ctx.SettingsPath })
                Assert.StartsWith(Path.GetFullPath(h.DataDir), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

            var settingsBefore = File.ReadAllBytes(ctx.SettingsPath);
            h.Call("play_ability", Args(Combo, ("ability", "200"), ("wait_seconds", 20)));
            h.Call("preview_state", Args(Combo, ("state", "200"), ("wait_seconds", 20)));
            h.Call("create_experiment", Args(Combo, ("name", "Isolation"), ("spec", "200 > 210")));
            h.Call("test_sequence", Args(Combo, ("spec", "200 > 210"), ("setup", new JsonObject { ["approach_distance"] = 70 }), ("wait_seconds", 30)));
            Assert.Equal(settingsBefore, File.ReadAllBytes(ctx.SettingsPath));          // a per-call setup never changes settings.json
            Assert.False(Directory.Exists(ctx.AppPlaybackRoot));                       // the Play Combo / Ability history is never written
        }

        Assert.Equal(before, Snapshot(watched));
    }

    [Fact]
    public void ManyMcpExperimentsNeverEvictTheUsersOwnExperiments()
    {
        using var h = new McpHarness();
        var store = h.App.Context.Experiments;
        var scope = new ExperimentScope("char:ComboGuy", "HASH", null, "IkemenGuy", "stages/ring.def", 60, "seq-user", 1, "Mine", "FP");
        void Add(string origin, int i)
        {
            var dir = store.NewExperimentDirectory(out var id);
            store.Save(new ExperimentSummary(id, DateTime.UtcNow.AddMinutes(i), scope, "x", 1, 1, false, []) { Directory = dir, Origin = origin });
        }

        for (var i = 0; i < 5; i++) Add(ExperimentSummary.AppOrigin, i);
        for (var i = 0; i < ExperimentStore.KeepExperiments + 8; i++) Add(ExperimentSummary.McpOrigin, 100 + i);
        store.Prune();
        var left = store.List();
        Assert.Equal(5, left.Count(e => e.Origin == ExperimentSummary.AppOrigin));
        Assert.Equal(ExperimentStore.KeepExperiments, left.Count(e => e.Origin == ExperimentSummary.McpOrigin));
    }

    private static string Snapshot(IEnumerable<string> paths) => string.Join("\n", paths.SelectMany(p =>
        File.Exists(p) ? [$"{p}|{new FileInfo(p).Length}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}"]
        : Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => $"{f}|{new FileInfo(f).Length}|{new FileInfo(f).LastWriteTimeUtc.Ticks}")
        : [$"{p}|absent"]));
}
