using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;

namespace IKEMENLab.Core.XRay.Behavior;

/// <summary>A finished watched match: the committed recording and what behavior recognition read from it.</summary>
public sealed record WatchOutcome(PlaybackRecord Record, TraceLog Log, BehaviorRun Run);

/// <summary>
/// Watched runs (Watch &amp; Ask), <c>%LOCALAPPDATA%\IKEMEN Lab\xray-watch\</c>: one record folder per run (meta.json, trace.jsonl, behavior.json). It never
/// touches the Play Combo / Ability history or the experiment store. Retention keeps the newest <see cref="KeepRunsPerOrigin"/> runs of each origin (app,
/// cli, mcp), so an agent's runs never evict the user's.
/// </summary>
public sealed class BehaviorStore
{
    public const int KeepRunsPerOrigin = 40;
    public const string FileName = "behavior.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Stamp, BehaviorRun Run)> _cache = new(StringComparer.Ordinal);

    public BehaviorStore(string? root = null) => Root = Path.GetFullPath(root ?? DefaultRoot);
    public static string DefaultRoot => Path.Combine(AppDataPaths.GetAppDataDirectory(), "xray-watch");
    public string Root { get; }

    public static string ToJson(BehaviorRun run)
    {
        var node = JsonSerializer.SerializeToNode(run, Json)!.AsObject();
        node.Remove("directory");
        var wrapped = new System.Text.Json.Nodes.JsonObject { ["schema"] = BehaviorRun.SchemaVersion };
        foreach (var kv in node.ToList()) { node.Remove(kv.Key); wrapped[kv.Key] = kv.Value; }
        return wrapped.ToJsonString(Json).Replace("\r\n", "\n");
    }

    public static BehaviorRun FromJson(string json, string directory) =>
        (JsonSerializer.Deserialize<BehaviorRun>(json, Json) ?? throw new JsonException("empty behavior run")) with { Directory = directory };

    public void Write(string directory, BehaviorRun run)
    {
        var path = Path.Combine(directory, FileName);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, ToJson(run), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Committed runs (meta.json present), newest first; for <paramref name="character"/> (an index character id) when given. Runs stored by an older detector
    /// are re-detected from their trace, with <paramref name="superStates"/> (the character's current Super states) when the caller has the character loaded.
    /// </summary>
    public IReadOnlyList<BehaviorRun> List(string? character = null, IReadOnlySet<int>? superStates = null)
    {
        var list = new List<BehaviorRun>();
        if (!Directory.Exists(Root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (!File.Exists(Path.Combine(dir, "meta.json")) || Load(dir, superStates, character) is not { } run) continue;
            if (character is null || run.Character == character) list.Add(run);
        }

        return list.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id, StringComparer.Ordinal).ToList();
    }

    public BehaviorRun? Get(string id, IReadOnlySet<int>? superStates = null) =>
        id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..", StringComparison.Ordinal) ? null
        : File.Exists(Path.Combine(Root, id, "meta.json")) ? Load(Path.Combine(Root, id), superStates, null) : null;

    public TraceLog? Trace(BehaviorRun run) =>
        File.Exists(Path.Combine(run.Directory, "trace.jsonl")) ? TraceReader.ReadFile(Path.Combine(run.Directory, "trace.jsonl")) : null;

    /// <summary>Reads a run; one stored by an older detector is re-detected — with the caller's Super states only when it is the caller's character.</summary>
    private BehaviorRun? Load(string dir, IReadOnlySet<int>? superStates, string? character)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (_gate)
                if (_cache.TryGetValue(dir, out var hit) && hit.Stamp == stamp) return hit.Run;
            var run = FromJson(File.ReadAllText(path), dir);
            if (character is not null && run.Character != character) return run;   // another character's run: listed by someone else, never re-detected with these supers
            if (run.DetectorVersion != BehaviorDetector.Version && Trace(run) is { } log)
            {
                var supers = superStates ?? run.SuperStates.ToHashSet();
                run = run with
                {
                    Detection = BehaviorDetector.Detect(log, supers, run.SubjectAiLevel, run.Detection.SubjectLocalWidth), DetectorVersion = BehaviorDetector.Version,
                    SuperStates = supers.OrderBy(x => x).ToList()
                };
                Write(dir, run);
                stamp = File.GetLastWriteTimeUtc(path);
            }

            lock (_gate) _cache[dir] = (stamp, run);
            return run;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Keeps the newest <see cref="KeepRunsPerOrigin"/> runs of each origin; nothing outside this store is touched.</summary>
    public void Prune()
    {
        foreach (var old in List().GroupBy(r => r.Origin, StringComparer.Ordinal).SelectMany(g => g.Skip(KeepRunsPerOrigin)))
        {
            try { Directory.Delete(old.Directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }
}

/// <summary>Runs a watched match through the existing playback service and stores what behavior recognition reads from it.</summary>
public static class BehaviorWatch
{
    public const int DefaultSeconds = 60, MinSeconds = 10, MaxSeconds = 180, DefaultSubjectAi = 8, DefaultOpponentAi = 4;

    public static WatchRequest Request(string root, SemanticIndex index, string subjectFolder, string subjectDef, PlaybackSetup setup, int seconds, int subjectAi,
        int opponentAi, string name) =>
        new(root, index.CharacterId, subjectFolder, subjectDef, setup, Math.Clamp(seconds, MinSeconds, MaxSeconds), Math.Clamp(subjectAi, 1, 8), Math.Clamp(opponentAi, 1, 8),
            $"Watch · {name} vs {setup.Dummy} ({Math.Clamp(seconds, MinSeconds, MaxSeconds)} s)");

    public static WatchJob Job(WatchRequest request) => new(request.SubjectFolder, request.Setup, request.Seconds, request.Summary);

    /// <summary>Records the match into <paramref name="store"/> and returns the run. Cancel semantics are the playback service's (nothing is kept when the cancel wins).</summary>
    public static WatchOutcome Run(ComboPlaybackService playback, BehaviorStore store, WatchRequest request, SemanticIndex index, PlaybackCancellation gate,
        Action<string>? progress = null, string origin = ExperimentSummary.AppOrigin)
    {
        var supers = StaticBehavior.SuperStates(index);
        var hash = ExperimentScope.HashOf(index);
        var localWidth = BehaviorCoordinates.LocalWidth(Path.Combine(request.Root, "chars", request.SubjectDef));
        BehaviorRun? run = null;
        var service = playback.WithStore(store.Root, int.MaxValue);
        var (record, log) = service.Watch(request, progress, gate, (dir, id, trace) =>
        {
            var detection = BehaviorDetector.Detect(trace, supers, request.SubjectAiLevel, localWidth);
            run = new BehaviorRun(id, DateTime.UtcNow, origin, request.Character, hash, trace.Meta?.EngineSha256, request.Setup.Dummy, request.Setup.Stage,
                request.SubjectAiLevel, request.OpponentAiLevel, request.Seconds, detection, supers.OrderBy(x => x).ToList()) { Directory = dir };
            store.Write(dir, run);
        });
        store.Prune();
        return new WatchOutcome(record, log, run!);
    }
}

/// <summary>The sha256 of an engine executable, cached by path, size and time (it is compared with the engine a stored run used).</summary>
public static class EngineIdentity
{
    private static readonly Dictionary<string, (long Size, DateTime Time, string Sha)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Sha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var info = new FileInfo(path);
        lock (Cache)
            if (Cache.TryGetValue(info.FullName, out var hit) && hit.Size == info.Length && hit.Time == info.LastWriteTimeUtc) return hit.Sha;
        try
        {
            using var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
            lock (Cache) Cache[info.FullName] = (info.Length, info.LastWriteTimeUtc, sha);
            return sha;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
