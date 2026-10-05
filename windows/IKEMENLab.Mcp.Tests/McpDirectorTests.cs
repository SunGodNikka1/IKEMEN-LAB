using System.Text.Json.Nodes;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Director;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Tests;
using Xunit;

namespace IKEMENLab.Mcp.Tests;

/// <summary>
/// Phase 6 through MCP — Observe + Experiment only: propose_behavior writes a Draft into IKEMEN Lab's data, inspect_behavior_draft reads it, test_behavior
/// runs the suite on a disposable staging copy. No tool approves, deploys, or touches the working copy or the installed character.
/// </summary>
public class McpDirectorTests
{
    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) o[k] = v?.DeepClone();
        return o;
    }

    private static readonly (string, JsonNode?) Combo = ("character", "ComboGuy");

    /// <summary>ComboGuy with walk animations, and a Sequence Lab experiment on it (on the harness's engine) that proves 1000 → chase → 200.</summary>
    private static (ExperimentSummary Experiment, SemanticIndex Index) Teachable(McpHarness h)
    {
        File.AppendAllText(Path.Combine(h.Root, "chars", "ComboGuy", "ComboGuy.air"), DirectorFixtures.Anims);
        var entry = new CharacterEntry { Id = "ComboGuy", DisplayName = "Combo Guy", Name = "Combo Guy", Author = "", VersionDate = "", DefPath = "chars/ComboGuy/ComboGuy.def", FolderPath = "chars/ComboGuy" };
        var index = CharacterSemanticIndexer.Build(h.Root, entry);
        var e = DirectorFixtures.Experiment(index, new ExperimentStore(Path.Combine(h.DataDir, "xray-experiments")), engine: EngineIdentity.Sha256(h.EnginePath)!);
        return (e, index);
    }

    [Fact]
    public void ProposeAndInspectWriteOnlyADraftAndShowTheCardProofOwnershipAndRules()
    {
        using var h = new McpHarness();
        var (e, _) = Teachable(h);
        var installed = Path.Combine(h.Root, "chars", "ComboGuy");
        var before = DirectorWorkspace.HashFolder(installed);

        var draft = h.Call("propose_behavior", Args(Combo, ("from_experiment", e.Id), ("frequency", "often")));
        var id = draft["id"]!.GetValue<string>();
        Assert.Equal(("Draft", 1), (draft["status"]!.GetValue<string>(), draft["revision"]!.GetValue<int>()));
        var card = draft["card"]!;
        Assert.Contains("lying", card["when"]!.GetValue<string>());
        Assert.Contains("x (LP)", card["then"]!.GetValue<string>());
        Assert.Contains("35", card["do"]!.GetValue<string>());
        Assert.Contains("Often", card["howOften"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.True(draft["followUpProof"]!["proven"]!.GetValue<bool>());
        Assert.Equal("Owned", draft["ownership"]!["verdict"]!.GetValue<string>());
        Assert.Null(draft["problems"]);
        Assert.Contains(draft["generated"]!["rules"]!.AsArray(), r => r!["trigger"]!.GetValue<string>().Contains("P2Dist X", StringComparison.Ordinal));
        Assert.Contains("Approval and deployment happen only in the app", draft["next"]!.GetValue<string>());

        // Only the draft was written: no working copy, the installed character byte-identical.
        Assert.Equal(before, DirectorWorkspace.HashFolder(installed));
        Assert.False(Directory.Exists(Path.Combine(h.DataDir, "ai-director", "ComboGuy", "working")));
        Assert.DoesNotContain(Directory.EnumerateFiles(installed), f => f.EndsWith("ikemenlab_director.cns", StringComparison.OrdinalIgnoreCase));

        var list = h.Call("inspect_behavior_draft", Args(Combo));
        var row = Assert.Single(list["taughtBehaviors"]!.AsArray())!;
        Assert.Equal((id, "Draft"), (row["id"]!.GetValue<string>(), row["status"]!.GetValue<string>()));
        var full = h.Call("inspect_behavior_draft", Args(Combo, ("behavior_id", id), ("include_code", true)));
        Assert.Contains("Knockdown Chase: start", full["generatedCode"]!["stateMinus1Block"]!.GetValue<string>());
        Assert.Contains("[Statedef ", full["generatedCode"]!["statesFile"]!.GetValue<string>());
        Assert.Null(full["workingCopy"]);
        Assert.Equal("none (approval is the user's, in the app)", full["approval"]!.GetValue<string>());

        // Bad answers are refused with the allowed values; an unknown behavior is an error.
        Assert.Contains("falling, lying, both", h.Error("propose_behavior", Args(Combo, ("from_experiment", e.Id), ("when", "sideways")))["error"]!.ToJsonString());
        h.Error("inspect_behavior_draft", Args(Combo, ("behavior_id", "kc-nope")));
        h.Error("propose_behavior", Args(Combo, ("from_experiment", "20990101-000000-ffffff")));
    }

    [Fact]
    public void AnUnprovenFollowUpIsADraftWithProblemsAndCannotBeTested()
    {
        using var h = new McpHarness();
        var (_, index) = Teachable(h);
        var draft = h.Call("propose_behavior", Args(Combo, ("follow_up", TeachAi.AbilityOf(index, 210))));
        Assert.False(draft["followUpProof"]!["proven"]!.GetValue<bool>());
        Assert.Contains(draft["problems"]!.AsArray(), p => p!.GetValue<string>().Contains("no runtime proof", StringComparison.Ordinal));
        var refused = h.Error("test_behavior", Args(Combo, ("behavior_id", draft["id"]!.GetValue<string>())));
        Assert.Contains("cannot be tested yet", refused.ToJsonString());
        Assert.Equal(0, h.Engine.Runs);
    }

    [Fact]
    public void TestBehaviorRunsTheSuiteOnAStagingCopyAndNeverTheWorkingCopyOrTheInstall()
    {
        using var h = new McpHarness();
        var (e, _) = Teachable(h);
        var installed = Path.Combine(h.Root, "chars", "ComboGuy");
        var before = DirectorWorkspace.HashFolder(installed);
        var id = h.Call("propose_behavior", Args(Combo, ("from_experiment", e.Id), ("trials", 1)))["id"]!.GetValue<string>();

        var job = h.Call("test_behavior", Args(Combo, ("behavior_id", id), ("wait_seconds", 30)));
        Assert.Equal("completed", McpHarness.Status(job));
        var r = job["result"]!;
        var runs = r["runs"]!.AsArray();
        Assert.Equal(["Proven setup (idle opponent)", "Opponent active after the knockdown", "Natural match (30 s)"], runs.Select(x => x!["setup"]!.GetValue<string>()));
        Assert.Equal((1, 1, 1), (runs[0]!["chases"]!.GetValue<int>(), runs[0]!["followUps"]!.GetValue<int>(), runs[0]!["connected"]!.GetValue<int>()));
        Assert.Equal(1, runs[1]!["fallbacks"]!.GetValue<int>());
        Assert.Equal(2, r["regressions"]!.AsArray().Count);                                                   // the proven sequence and the follow-up
        Assert.Contains("these setups", r["scope"]!.GetValue<string>());
        // Pass / fail is the suite's own judgement (here the mock engine cannot replay the proven sequence, so that regression fails). Either way an MCP
        // test ran on a staging build, so the behavior stays in Testing: approval binds to the working copy, which only the app's Test builds.
        var passed = r["passed"]!.GetValue<bool>();
        Assert.Equal(passed, r["regressions"]!.AsArray().All(g => g!.GetValue<string>().StartsWith("held", StringComparison.Ordinal)));
        Assert.Equal("Testing", r["status"]!.GetValue<string>());
        Assert.Contains("Approval binds to the working copy", r["next"]!.GetValue<string>());

        // Every run loaded the staging build (the generated file is in the sandbox), and nothing was left behind or written to the install.
        Assert.All(h.Engine.Sandboxes, s => Assert.False(Directory.Exists(s)));
        Assert.Equal(before, DirectorWorkspace.HashFolder(installed));
        Assert.False(Directory.Exists(Path.Combine(h.DataDir, "ai-director", "ComboGuy", "working")));
        var staging = Path.Combine(h.DataDir, "ai-director", "ComboGuy", "staging");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());

        // The test is recorded on the behavior; inspect shows it, still with no approval.
        var card = h.Call("inspect_behavior_draft", Args(Combo, ("behavior_id", id)));
        Assert.Equal(r["testId"]!.GetValue<string>(), card["lastTest"]!["testId"]!.GetValue<string>());
        Assert.Equal("none (approval is the user's, in the app)", card["approval"]!.GetValue<string>());
        Assert.Equal("none", card["deployment"]!.GetValue<string>());
    }
}
