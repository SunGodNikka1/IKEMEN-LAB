using System.Security.Cryptography;
using System.Text.Json.Nodes;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Behavior;
using Xunit;

namespace IKEMENLab.Mcp.Tests;

/// <summary>
/// Phase 5 through MCP: watch_match records a match through the existing playback session into the watched-run store; list_behaviors / inspect_behavior
/// show Core's evidence levels and cards; why_did_ai_do_this answers from Core's bounded explainer and never names a cause.
/// </summary>
public class McpBehaviorTests
{
    private static JsonObject Args(params (string Key, JsonNode? Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) o[k] = v?.DeepClone();
        return o;
    }

    private static readonly (string, JsonNode?) Combo = ("character", "ComboGuy");

    private static JsonObject Behavior(JsonObject list, string name) =>
        list["behaviors"]!.AsArray().Single(b => b!["name"]!.GetValue<string>() == name)!.AsObject();

    [Fact]
    public void AWatchedMatchTurnsIntoObservedBehaviorCardsAndBoundedWhyAnswers()
    {
        using var h = new McpHarness();
        var before = h.Call("list_behaviors", Args(Combo));
        Assert.Equal(0, before["watchedRuns"]!["onTheseFilesAndEngine"]!.GetValue<int>());
        Assert.Equal("Not seen", Behavior(before, "Knockdown Chase")["level"]!.GetValue<string>());
        Assert.Equal(10, before["behaviors"]!.AsArray().Count);

        var job = h.Call("watch_match", Args(Combo, ("seconds", 30), ("opponent_ai_level", 1), ("wait_seconds", 30)));
        Assert.Equal("completed", McpHarness.Status(job));
        var r = job["result"]!;
        var runId = r["runId"]!.GetValue<string>();
        Assert.Equal("-p1 ComboGuy -p2 IkemenGuy -s stages/ring.def -p1.ai 8 -p2.ai 1 -nosound", h.Engine.WatchArguments);
        var chase = r["episodes"]!.AsArray().Single(e => e!["behavior"]!.GetValue<string>() == "Knockdown Chase")!;
        Assert.Equal("16–55", chase["frames"]!.GetValue<string>());
        Assert.Equal("Connected", chase["outcome"]!.GetValue<string>());
        Assert.Contains("Distance 120 → 84", chase["chain"]!.GetValue<string>());
        Assert.Contains("AI level 8", r["control"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(h.DataDir, "xray-watch", runId, "behavior.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.Sandboxes));

        // One run on the current files and engine: Observed, never Confirmed (that needs repeats across runs and setups).
        var after = h.Call("list_behaviors", Args(Combo));
        Assert.True(after["engineKnown"]!.GetValue<bool>());
        var kc = Behavior(after, "Knockdown Chase");
        Assert.Equal(("Observed", 1, 1), (kc["level"]!.GetValue<string>(), kc["episodes"]!.GetValue<int>(), kc["runs"]!.GetValue<int>()));
        Assert.Contains("Confirmed Pattern = at least 3 complete episodes", after["levels"]!.GetValue<string>());

        var card = h.Call("inspect_behavior", Args(Combo, ("behavior", "knockdown chase")));
        Assert.Equal("Observed", card["level"]!.GetValue<string>());
        Assert.Equal("knockdown → approach → attack", card["pattern"]!.GetValue<string>());
        Assert.Contains(card["conditions"]!.AsArray(), c => c!.GetValue<string>().StartsWith("Enemy Lying", StringComparison.Ordinal));
        var recent = card["runtime"]!["recent"]!.AsArray().Single()!;
        Assert.Equal(runId, recent["runId"]!.GetValue<string>());
        Assert.Equal("vs IkemenGuy on ring (opponent AI 1)", recent["setup"]!.GetValue<string>());
        Assert.Contains(card["linksTo"]!.AsArray(), l => l!["id"]!.GetValue<string>() == "state:20");
        Assert.Contains(card["limitations"]!.AsArray(), l => l!.GetValue<string>().Contains("fighter's own movement", StringComparison.Ordinal));
        Assert.Contains("not evidence of absence", card["static"]!["note"]!.GetValue<string>());   // ComboGuy has no AI rules

        // Why: observed context, the matching pattern, and an explicit refusal to name a cause.
        var why = h.Call("why_did_ai_do_this", Args(Combo, ("run_id", runId), ("frame", 33)));
        Assert.False(why["cause"]!["known"]!.GetValue<bool>());
        Assert.Equal(BehaviorWhy.NoAttribution, why["cause"]!["statement"]!.GetValue<string>());
        Assert.Contains("Enemy Lying", why["conditionsHeld"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal("Knockdown Chase", why["matchesPattern"]![0]!["behavior"]!.GetValue<string>());
        Assert.Contains("This matches the Knockdown Chase and Ground Follow-Up patterns.", why["summary"]!.GetValue<string>());   // the hit on the lying opponent is both
        Assert.DoesNotContain("because", why["summary"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("on the current character files and engine", why["runEvidence"]!["current"]!.GetValue<string>());
        Assert.NotEmpty(why["nearby"]!.AsArray());
        h.Error("why_did_ai_do_this", Args(Combo, ("run_id", "20990101-000000-ffffff"), ("frame", 1)));

        // The watched run's trace is also readable as a runtime trace slice.
        var trace = h.Call("get_runtime_trace", Args(("run_id", runId), ("from_frame", 30), ("to_frame", 60), ("max_events", 10)));
        Assert.Equal("watched runs", trace["store"]!.GetValue<string>());
        Assert.Equal("watch", trace["run"]!["mode"]!.GetValue<string>());

        // Renaming in the app shows up in the next answer.
        Assert.Null(h.App.Context.Load("ComboGuy").Names.Rename("state:20", "Walk Forward"));
        var renamed = h.Call("inspect_behavior", Args(Combo, ("behavior", "Knockdown Chase")));
        Assert.Contains("Walk Forward (20)", renamed["runtime"]!["recent"]![0]!["chain"]!.GetValue<string>());

        // Editing the character's files makes that runtime evidence stale.
        File.AppendAllText(Path.Combine(h.Root, "chars", "ComboGuy", "ComboGuy.cns"), "\n; edited\n");
        var stale = Behavior(h.Call("list_behaviors", Args(Combo)), "Knockdown Chase");
        Assert.Equal(("Not seen", 0, 1), (stale["level"]!.GetValue<string>(), stale["episodes"]!.GetValue<int>(), stale["staleEpisodes"]!.GetValue<int>()));
        var staleWhy = h.Call("why_did_ai_do_this", Args(Combo, ("run_id", runId), ("frame", 33)));
        Assert.StartsWith("stale: the character's files changed", staleWhy["runEvidence"]!["current"]!.GetValue<string>());
    }

    [Fact]
    public void AWatchedMatchCanBeCancelledAndLeavesNothingBehind()
    {
        using var h = new McpHarness();
        h.Engine.Block = true;
        var job = h.Call("watch_match", Args(Combo, ("seconds", 20)));
        Assert.True(h.Engine.Entered.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(h.Call("cancel_job", Args(("job_id", job["jobId"]!)))["accepted"]!.GetValue<bool>());
        Assert.Equal("cancelled", McpHarness.Status(h.Finish(job["jobId"]!.GetValue<string>())));
        Assert.False(Directory.Exists(Path.Combine(h.DataDir, "xray-watch")) && Directory.EnumerateDirectories(Path.Combine(h.DataDir, "xray-watch")).Any());
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.Sandboxes));
        Assert.Equal(0, h.Call("list_behaviors", Args(Combo))["watchedRuns"]!["onTheseFilesAndEngine"]!.GetValue<int>());
    }

    [Fact]
    public void BehaviorToolsNeverTouchTheUsersRealStores()
    {
        var real = AppDataPaths.GetAppDataDirectory();
        var watched = new[] { "xray-watch", "xray-playback", "xray-experiments", Path.Combine("xray", "names"), "settings.json" }.Select(p => Path.Combine(real, p)).ToList();
        string Snapshot() => string.Join("\n", watched.SelectMany(p =>
            File.Exists(p) ? [$"{p}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}"]
            : Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).Select(f => $"{f}|{new FileInfo(f).Length}|{new FileInfo(f).LastWriteTimeUtc.Ticks}")
            : [$"{p}|absent"]));
        var before = Snapshot();
        using (var h = new McpHarness())
        {
            Assert.StartsWith(Path.GetFullPath(h.DataDir), h.App.Context.Behavior.Root, StringComparison.OrdinalIgnoreCase);
            var job = h.Call("watch_match", Args(Combo, ("seconds", 15), ("wait_seconds", 30)));
            var runId = job["result"]!["runId"]!.GetValue<string>();
            h.Call("list_behaviors", Args(Combo));
            h.Call("inspect_behavior", Args(Combo, ("behavior", "Anti-Air")));
            h.Call("why_did_ai_do_this", Args(Combo, ("run_id", runId), ("frame", 40)));
            var stored = Assert.Single(h.App.Context.Behavior.List());
            Assert.Equal("mcp", stored.Origin);                                      // MCP runs never evict the user's (per-origin retention)
        }

        Assert.Equal(before, Snapshot());
    }
}
