using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Phase 4: the one-engine broker shared by the desktop app, the CLI and the MCP server, the app session's refusal while another client holds the engine,
/// and experiment origins (an agent's experiments never evict the user's own).
/// </summary>
public class XRayRuntimeBrokerTests : IDisposable
{
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private string Temp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    private sealed class CountingRunner : IEngineRunner
    {
        public int Runs;
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) { Runs++; return new EngineRunResult(0, false, null); }
    }

    [Fact]
    public void TheBrokerGivesTheEngineToOneClientAtATimeAndSaysWhoHasIt()
    {
        var path = Path.Combine(Temp("xray-lock-"), "engine.lock");
        var app = new RuntimeBroker(path);
        var mcp = new RuntimeBroker(path);                                             // another process, as far as the lock is concerned

        Assert.Null(app.CurrentHolder());
        using (var lease = app.TryAcquire(new RuntimeHolder("IKEMEN Lab app", "Play Ability · Kung Fu Palm", 1234, DateTime.UtcNow), out var none))
        {
            Assert.NotNull(lease);
            Assert.Null(none);
            Assert.Null(mcp.TryAcquire(RuntimeHolder.Now("MCP · claude-code", "test_sequence"), out var busy));
            Assert.Equal(("IKEMEN Lab app", "Play Ability · Kung Fu Palm", 1234), (busy!.Client, busy.Job, busy.ProcessId));
            Assert.Null(app.TryAcquire(RuntimeHolder.Now("IKEMEN Lab app", "a second window"), out _));   // not re-entrant: two windows cannot both play
            var ex = Assert.Throws<RuntimeBusyException>(() => mcp.Acquire(RuntimeHolder.Now("ikemenlab CLI", "runtime-sequence")));
            Assert.Contains("IKEMEN Lab app — Play Ability · Kung Fu Palm", ex.Message);
            Assert.Equal("IKEMEN Lab app", mcp.CurrentHolder()!.Client);
        }

        Assert.Null(mcp.CurrentHolder());
        using var second = mcp.Acquire(RuntimeHolder.Now("MCP · claude-code", "test_sequence"));
        Assert.Equal("MCP · claude-code", app.CurrentHolder()!.Client);
    }

    [Fact]
    public void ALockFileLeftByACrashedHolderDoesNotLockTheEngine()
    {
        var path = Path.Combine(Temp("xray-lock-"), "engine.lock");
        File.WriteAllText(path, """{"schema":"ikemenlab.runtime-lock/1","client":"IKEMEN Lab app","job":"crashed","processId":99999,"startedUtc":"2026-01-01T00:00:00Z"}""");
        var broker = new RuntimeBroker(path);
        Assert.Null(broker.CurrentHolder());                                          // nobody holds the handle: free
        using var lease = broker.Acquire(RuntimeHolder.Now("MCP · codex", "play_ability"));
        Assert.Equal("MCP · codex", new RuntimeBroker(path).CurrentHolder()!.Client);
    }

    [Fact]
    public void OnlyTheRealEngineRunnerTakesTheSharedLease()
    {
        Assert.Same(RuntimeBroker.Shared, new ComboPlaybackService(storeRoot: Temp("xray-pb-")).Broker);
        Assert.Null(new ComboPlaybackService(new CountingRunner(), Temp("xray-pb-")).Broker);
        var custom = new RuntimeBroker(Path.Combine(Temp("xray-lock-"), "engine.lock"));
        var service = new ComboPlaybackService(new CountingRunner(), Temp("xray-pb-")) { Broker = custom };
        Assert.Same(custom, service.WithStore(Temp("xray-pb-"), 5).Broker);
    }

    [Fact]
    public async Task TheAppSessionRefusesToLaunchWhileAnotherClientHoldsTheEngineAndHoldsItWhileItRuns()
    {
        var path = Path.Combine(Temp("xray-lock-"), "engine.lock");
        var broker = new RuntimeBroker(path);
        var runner = new CountingRunner();
        var service = new ComboPlaybackService(runner, Temp("xray-pb-")) { Broker = broker };
        var session = new PlaybackSession(service);
        var setup = new PlaybackSetup(Temp("xray-root-"), "kfm", "kfm/kfm.def", "stages/ring.def", "engine.exe", null, true, [], []);
        var job = new SequenceJob("seq-x@v1", "Palm, chase, jab", setup, 1, "Palm → Chase → Jab");
        var scope = new ExperimentScope("char:kfm", "H", null, "kfm", "stages/ring.def", 60, "seq-x", 1, "Palm, chase, jab", "FP");
        var worked = 0;
        RuntimeHolder? during = null;
        ExperimentOutcome Work(PlaybackCancellation gate, Action<string> progress)
        {
            worked++;
            during = new RuntimeBroker(path).CurrentHolder();
            return new ExperimentOutcome(new ExperimentSummary("e", DateTime.UtcNow, scope, "x", 1, 1, false, []), null);
        }

        using (new RuntimeBroker(path).Acquire(new RuntimeHolder("MCP · claude-code", "test_sequence · Kung Fu Palm → Normal A", 777, DateTime.UtcNow)))
        {
            await session.RunSequenceAsync(job, Work);
            Assert.Equal(0, worked);                                                  // nothing launched
            Assert.Equal(PlaybackState.Error, session.State);
            Assert.Contains("busy", session.Error);
            Assert.Contains("MCP · claude-code — test_sequence · Kung Fu Palm → Normal A", session.Error);
            Assert.Equal(VerdictWaitKind.EndedWithoutVerdict, session.VerdictForLatestAttempt().Kind);
            Assert.StartsWith("Could not play: The playback engine is busy", session.Headline);
        }

        await session.RunSequenceAsync(job, Work);
        Assert.Equal((1, PlaybackState.Finished), (worked, session.State));
        Assert.Equal(("IKEMEN Lab app", "Palm → Chase → Jab"), (during!.Client, during.Job));   // the app held the engine for the whole job
        Assert.Null(broker.CurrentHolder());                                          // and released it afterwards
        Assert.Equal(0, runner.Runs);
    }

    [Fact]
    public void ExperimentsRememberWhoRanThemAndEachOriginKeepsItsOwnNewest()
    {
        var store = new ExperimentStore(Temp("xray-exp-"));
        var scope = new ExperimentScope("char:kfm", "H", null, "kfm", "stages/ring.def", 60, "seq-x", 1, "Mine", "FP");
        var legacy = store.NewExperimentDirectory(out var legacyId);
        File.WriteAllText(Path.Combine(legacy, "experiment.json"), ExperimentStore.ToJson(new ExperimentSummary(legacyId, DateTime.UtcNow.AddDays(-1), scope, "x", 1, 1, false, []))
            .Replace("\"origin\": \"app\",", string.Empty));                          // written before origins existed
        for (var i = 0; i < ExperimentStore.KeepExperiments + 3; i++)
        {
            var dir = store.NewExperimentDirectory(out var id);
            store.Save(new ExperimentSummary(id, DateTime.UtcNow.AddMinutes(i), scope, "x", 1, 1, false, []) { Directory = dir, Origin = ExperimentSummary.CliOrigin });
        }

        store.Prune();
        var all = store.List();
        Assert.Equal(ExperimentSummary.AppOrigin, all.Single(e => e.Id == legacyId).Origin);   // the user's (older) experiment survives
        Assert.Equal(ExperimentStore.KeepExperiments, all.Count(e => e.Origin == ExperimentSummary.CliOrigin));
    }
}
