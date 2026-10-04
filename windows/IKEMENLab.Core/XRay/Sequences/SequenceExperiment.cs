using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Sequences;

/// <summary>
/// Exactly what an experiment's results belong to: the character's files (content hash), the engine that ran, the opponent and stage, the approach
/// distance, and the sequence version (with its plan fingerprint). Results are never shown as belonging to a different scope.
/// </summary>
public sealed record ExperimentScope(
    string Character, string CharacterHash, string? EngineSha256, string? Dummy, string? Stage, int ApproachDistance,
    string SequenceId, int SequenceVersion, string SequenceName, string PlanFingerprint)
{
    /// <summary>The same character files, engine, opponent, stage and approach distance (the sequence itself may differ: that is what a comparison compares).</summary>
    public bool SameSetup(ExperimentScope other) =>
        CharacterHash == other.CharacterHash && string.Equals(EngineSha256, other.EngineSha256, StringComparison.OrdinalIgnoreCase) && Dummy == other.Dummy &&
        Stage == other.Stage && ApproachDistance == other.ApproachDistance;

    public static string HashOf(SemanticIndex index) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", index.Files.Select(f => f.RelPath + ":" + f.Hash).OrderBy(x => x, StringComparer.Ordinal)))))[..16];
}

public sealed record ExperimentTrial(
    int Number, string RecordId, string Verdict, string? Reason, int? FailedStep, string? FailedLabel, int Attacks, int ConnectedAttacks, double? Damage,
    double? EndDistance, string? EndOpponent, int? OutOfHitstunFrames, double Seconds, string? EngineSha256);

/// <summary>An experiment: one sequence version run N times against one setup, every trial replaying the whole sequence from the start.</summary>
public sealed record ExperimentSummary(
    string Id, DateTime CreatedUtc, ExperimentScope Scope, string Steps, int Requested, int Completed, bool Stopped, IReadOnlyList<ExperimentTrial> Trials)
{
    public const string SchemaVersion = "ikemenlab.xray.experiment/1";
    public string Directory { get; init; } = string.Empty;
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Who ran it: <see cref="AppOrigin"/> (the Sequence Lab; also every experiment saved before origins existed), <see cref="CliOrigin"/> or <see cref="McpOrigin"/>.</summary>
    public string Origin { get; init; } = AppOrigin;
    public const string AppOrigin = "app", CliOrigin = "cli", McpOrigin = "mcp";

    public int Successes => Trials.Count(t => t.Verdict is nameof(SequenceVerdict.TrueCombo) or nameof(SequenceVerdict.ConnectedSequence));
    public int TrueCombos => Trials.Count(t => t.Verdict == nameof(SequenceVerdict.TrueCombo));
    public int Untestable => Trials.Count(t => t.Verdict == nameof(SequenceVerdict.CouldNotTest));
    public int AttacksTried => Trials.Sum(t => t.Attacks);
    public int AttacksConnected => Trials.Sum(t => t.ConnectedAttacks);
    public double? SuccessRate => Completed == 0 ? null : (double)Successes / Completed;
    public double? TrueComboRate => Completed == 0 ? null : (double)TrueCombos / Completed;
    public double? ConnectionRate => AttacksTried == 0 ? null : (double)AttacksConnected / AttacksTried;

    /// <summary>Why trials did not succeed, most common first ("Step 3 (Normal A): OutOfReach" → count).</summary>
    public IReadOnlyList<(string Reason, int Count)> FailureReasons => Trials.Where(t => t.Verdict is nameof(SequenceVerdict.DidNotConnect) or nameof(SequenceVerdict.CouldNotTest))
        .GroupBy(t => (t.FailedStep is { } s ? $"Step {s}" + (t.FailedLabel is { } l ? $" ({l})" : string.Empty) + ": " : string.Empty) + (t.Reason ?? t.Verdict))
        .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();

    public (double Min, double Average, double Max)? DamageRange => Range(Trials.Where(t => t.Damage is not null).Select(t => t.Damage!.Value));
    public (double Min, double Average, double Max)? EndDistanceRange => Range(Trials.Where(t => t.EndDistance is not null).Select(t => t.EndDistance!.Value));
    public IReadOnlyList<(string Posture, int Count)> EndOpponent => Trials.Where(t => t.EndOpponent is not null).GroupBy(t => t.EndOpponent!)
        .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
    public double? AverageSeconds => Trials.Count == 0 ? null : Trials.Average(t => t.Seconds);

    private static (double, double, double)? Range(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : (list.Min(), list.Average(), list.Max());
    }
}

/// <summary>The result the session publishes for a Sequence Lab run: the experiment summary and the latest trial in full.</summary>
public sealed record ExperimentOutcome(ExperimentSummary Summary, SequenceOutcome? LastTrial);

/// <summary>
/// Everything one run of a saved sequence version needs, built by <see cref="SequenceExperimentRunner.Prepare"/>: the plan, the trial request, the exact
/// scope its results belong to, and the session job. The Sequence Lab, the CLI and the MCP server all build their runs through it.
/// </summary>
public sealed record PreparedSequenceRun(Sequence Sequence, SequencePlanResult Planned, SequenceRunRequest Request, ExperimentScope Scope, SequenceJob Job, string Chain, int Trials);

/// <summary>
/// The isolated experiment store, <c>%LOCALAPPDATA%\IKEMEN Lab\xray-experiments\</c>: one folder per experiment (<c>experiment.json</c> and a
/// <c>trials\</c> folder with a full record per trial). It never reads, writes or prunes the Play Combo / Play Ability history in <c>xray-playback\</c>;
/// it keeps its newest <see cref="KeepExperiments"/> experiments, each with all of its trials.
/// </summary>
public sealed class ExperimentStore
{
    public const int KeepExperiments = 30;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IncludeFields = true
    };

    public ExperimentStore(string? root = null) => Root = Path.GetFullPath(root ?? DefaultRoot);
    public static string DefaultRoot => Path.Combine(AppDataPaths.GetAppDataDirectory(), "xray-experiments");
    public string Root { get; }

    public string NewExperimentDirectory(out string id)
    {
        id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(Root, id);
        System.IO.Directory.CreateDirectory(Path.Combine(dir, "trials"));
        return dir;
    }

    /// <summary>Writes experiment.json atomically (after every trial, so a stopped or crashed experiment keeps what it finished).</summary>
    public void Save(ExperimentSummary summary)
    {
        var path = Path.Combine(summary.Directory, "experiment.json");
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, ToJson(summary), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static string ToJson(ExperimentSummary s)
    {
        var doc = new
        {
            schema = ExperimentSummary.SchemaVersion, s.Id, s.CreatedUtc, s.Origin, s.Scope, s.Steps, s.Requested, s.Completed, s.Stopped, s.Trials, s.Notes,
            stats = new
            {
                s.Successes, s.TrueCombos, s.Untestable, s.AttacksTried, s.AttacksConnected, s.SuccessRate, s.TrueComboRate, s.ConnectionRate,
                failureReasons = s.FailureReasons.Select(r => new { reason = r.Reason, count = r.Count }),
                damage = s.DamageRange is { } d ? new { min = d.Min, average = d.Average, max = d.Max } : null,
                endDistance = s.EndDistanceRange is { } e ? new { min = e.Min, average = e.Average, max = e.Max } : null,
                endOpponent = s.EndOpponent.Select(o => new { posture = o.Posture, count = o.Count }),
                s.AverageSeconds
            }
        };
        return JsonSerializer.Serialize(doc, Json).Replace("\r\n", "\n");
    }

    /// <summary>Saved experiments, newest first; unreadable folders are skipped. Filtered by character hash and/or sequence id when given.</summary>
    public IReadOnlyList<ExperimentSummary> List(string? characterHash = null, string? sequenceId = null)
    {
        var list = new List<ExperimentSummary>();
        if (!System.IO.Directory.Exists(Root)) return list;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(Root))
        {
            var path = Path.Combine(dir, "experiment.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                var scope = r.GetProperty("scope").Deserialize<ExperimentScope>(Json)!;
                if (characterHash is not null && scope.CharacterHash != characterHash) continue;
                if (sequenceId is not null && scope.SequenceId != sequenceId) continue;
                var trials = r.GetProperty("trials").Deserialize<List<ExperimentTrial>>(Json) ?? [];
                list.Add(new ExperimentSummary(r.GetProperty("id").GetString()!, r.GetProperty("createdUtc").GetDateTime(), scope, r.GetProperty("steps").GetString() ?? string.Empty,
                    r.GetProperty("requested").GetInt32(), r.GetProperty("completed").GetInt32(), r.GetProperty("stopped").GetBoolean(), trials)
                {
                    Directory = dir, Notes = r.TryGetProperty("notes", out var n) ? n.Deserialize<List<string>>(Json) ?? [] : [],
                    Origin = r.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.String && o.GetString() is { Length: > 0 } origin ? origin : ExperimentSummary.AppOrigin
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException) { /* skip */ }
        }

        return list.OrderByDescending(e => e.CreatedUtc).ThenByDescending(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Keeps the newest <see cref="KeepExperiments"/> experiments of EACH origin in THIS store; nothing outside it is touched. Experiments run by the
    /// CLI or an MCP agent therefore never evict the user's own Sequence Lab experiments (and the reverse), and no retention is raised for anyone.
    /// </summary>
    public void Prune()
    {
        foreach (var old in List().GroupBy(e => e.Origin, StringComparer.Ordinal).SelectMany(g => g.Skip(KeepExperiments)))
        {
            try { System.IO.Directory.Delete(old.Directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }
}

/// <summary>Runs an experiment: N trials of one sequence version, each a full replay from neutral through the existing playback pipeline.</summary>
public static class SequenceExperimentRunner
{
    /// <summary>
    /// Plans <paramref name="sequence"/> (one plan for every trial) and builds its run against <paramref name="setup"/>. Returns no run, with the
    /// planner's refusal in <c>Planned</c>, when the sequence cannot be played.
    /// </summary>
    public static (PreparedSequenceRun? Run, SequencePlanResult Planned) Prepare(Combo.CandidateGraph graph, Sequence sequence, string root, string subjectFolder,
        string subjectDef, PlaybackSetup setup, int trials)
    {
        var index = graph.Index;
        var planned = SequencePlanner.Plan(graph, sequence, setup.ApproachDistance);
        if (planned.Plan is null) return (null, planned);
        var labels = planned.Steps.Select(s => s.Label).ToList();
        var chain = SequenceLabels.Chain(sequence.Actions, index);
        var request = new SequenceRunRequest(root, subjectFolder, subjectDef, planned.Plan, labels, setup, chain);
        var scope = new ExperimentScope(index.CharacterId, ExperimentScope.HashOf(index), null, setup.Dummy, setup.Stage, setup.ApproachDistance, sequence.Id, sequence.Version,
            sequence.Name, InputPlanner.Fingerprint(planned.Plan));
        var job = new SequenceJob(sequence.Key, sequence.Name, setup, trials, chain);
        return (new PreparedSequenceRun(sequence, planned, request, scope, job, chain, trials), planned);
    }

    /// <summary>Runs a prepared sequence (see <see cref="Run(ComboPlaybackService, ExperimentStore, SequenceRunRequest, ExperimentScope, string, int, PlaybackCancellation, Action{string}?, string)"/>).</summary>
    public static ExperimentOutcome Run(ComboPlaybackService playback, ExperimentStore store, PreparedSequenceRun run, PlaybackCancellation job, Action<string>? progress = null,
        string origin = ExperimentSummary.AppOrigin) =>
        Run(playback, store, run.Request, run.Scope, run.Chain, run.Trials, job, progress, origin);

    /// <summary>
    /// Runs the trials. Each trial has its own commit/cancel decision linked to <paramref name="job"/>: a cancel stops the trial in progress (no
    /// record for it) and no further trials start; finished trials stay recorded and the summary says the experiment was stopped. With no finished
    /// trial a cancel is rethrown, so nothing is published.
    /// </summary>
    public static ExperimentOutcome Run(ComboPlaybackService playback, ExperimentStore store, SequenceRunRequest request, ExperimentScope scope, string steps, int trials,
        PlaybackCancellation job, Action<string>? progress = null, string origin = ExperimentSummary.AppOrigin)
    {
        if (trials < 1) throw new ArgumentOutOfRangeException(nameof(trials));
        var dir = store.NewExperimentDirectory(out var id);
        var service = playback.WithStore(Path.Combine(dir, "trials"), int.MaxValue);
        var done = new List<ExperimentTrial>();
        var notes = new List<string>();
        SequenceOutcome? last = null;
        var stopped = false;
        var effective = scope;
        ExperimentSummary Summary() => new(id, DateTime.UtcNow, effective, steps, trials, done.Count, stopped, done.ToList()) { Directory = dir, Notes = notes.ToList(), Origin = origin };

        try
        {
            for (var n = 1; n <= trials; n++)
            {
                if (job.Token.IsCancellationRequested) { stopped = true; break; }
                progress?.Invoke($"trial:{n}/{trials}");
                SequenceOutcome outcome;
                try
                {
                    using var gate = new PlaybackCancellation(job.Token);
                    outcome = service.PlaySequence(request, progress, gate);
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    break;
                }

                last = outcome;
                var r = outcome.Report;
                if (done.Count == 0) effective = effective with { EngineSha256 = r.EngineSha256 };
                else if (!string.Equals(r.EngineSha256, effective.EngineSha256, StringComparison.OrdinalIgnoreCase))
                    notes.Add($"Trial {n} ran a different engine ({r.EngineSha256}) than trial 1 ({effective.EngineSha256}).");
                var failed = r.FailedStep is { } fs ? r.Steps.FirstOrDefault(s => s.Index == fs)?.Label : null;
                done.Add(new ExperimentTrial(n, outcome.Record.Id, r.Verdict.ToString(), r.Reason, r.FailedStep, failed, r.Attacks, r.ConnectedAttacks, r.Damage,
                    r.End?.Distance, r.End?.OpponentPosture, r.OutOfHitstunFrames, outcome.Record.Seconds, r.EngineSha256));
                store.Save(Summary());
            }
        }
        finally
        {
            // Commit the experiment as a whole, atomically with a Cancel: a cancel that loses this race is refused as "too late", like any run's.
            if (!stopped && done.Count == trials && !job.TryCommit(() => { })) stopped = true;
            if (done.Count > 0) store.Save(Summary());
            else TryDelete(dir);
            store.Prune();
        }

        if (done.Count == 0) throw new OperationCanceledException(job.Token);
        return new ExperimentOutcome(Summary(), last);
    }

    private static void TryDelete(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>What ×N costs: N engine launches, each about <paramref name="secondsPerTrial"/> (the last measured trial, or a conservative default).</summary>
    public static string CostText(int trials, double? secondsPerTrial)
    {
        var per = secondsPerTrial ?? 30;
        var total = TimeSpan.FromSeconds(per * trials);
        var minutes = total.TotalMinutes >= 1 ? $"about {Math.Ceiling(total.TotalMinutes):0} minute(s)" : $"about {Math.Ceiling(total.TotalSeconds):0} seconds";
        return $"×{trials} launches IKEMEN {trials} time(s), one after another — {minutes} at ~{per:0} s per trial" + (secondsPerTrial is null ? " (estimated; no trial measured yet)" : " (from the last trial)") +
               $"; each trial's record is kept in the experiment store (~0.3 MB each).";
    }
}

/// <summary>A saved sequence (variant) as stored: its actions in the compact spec form plus id, name, version and times.</summary>
public sealed class SavedSequence
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string Spec { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }

    public Sequence ToSequence() => new(Id, Name, Version, SequenceSpec.Parse(Spec));
}

public sealed class SequenceDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string CharacterFolder { get; set; } = string.Empty;
    public List<SavedSequence> Sequences { get; set; } = [];
}

/// <summary>
/// Saved sequences per character, in app data (<c>xray\sequences\</c>), keyed like the names store. Never in the character files. Saving an existing
/// sequence with different steps gives it a new version, so results of the old version stay attributable to it.
/// </summary>
public sealed class SequenceStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public SequenceStore(string directory) => Directory = Path.GetFullPath(directory);
    public static string DefaultDirectory => Path.Combine(AppDataPaths.GetAppDataDirectory(), "xray", "sequences");
    public static SequenceStore CreateDefault() => new(DefaultDirectory);
    public string Directory { get; }

    public string PathFor(string characterFolder)
    {
        var key = Names.NameOverlayStore.NormalizeFolder(characterFolder).ToLowerInvariant();
        return Path.Combine(Directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant() + ".json");
    }

    public SequenceDocument Load(string characterFolder)
    {
        lock (Gate)
        {
            var path = PathFor(characterFolder);
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<SequenceDocument>(File.ReadAllText(path), Json) is { } doc) return doc;
            }
            catch (JsonException)
            {
                File.Copy(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), overwrite: true);
            }

            return new SequenceDocument { CharacterFolder = Names.NameOverlayStore.NormalizeFolder(characterFolder) };
        }
    }

    /// <summary>Adds or updates <paramref name="sequence"/>; returns it with its stored version (bumped when its steps changed).</summary>
    public Sequence Save(string characterFolder, Sequence sequence)
    {
        lock (Gate)
        {
            var doc = Load(characterFolder);
            var spec = SequenceSpec.Format(sequence.Actions);
            var existing = doc.Sequences.FirstOrDefault(s => s.Id == sequence.Id);
            if (existing is null) doc.Sequences.Add(existing = new SavedSequence { Id = sequence.Id, Version = 1 });
            else if (existing.Spec != spec) existing.Version++;
            existing.Name = sequence.Name;
            existing.Spec = spec;
            existing.UpdatedUtc = DateTime.UtcNow;
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(characterFolder);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            File.Move(tmp, path, overwrite: true);
            return existing.ToSequence();
        }
    }

    public void Delete(string characterFolder, string id)
    {
        lock (Gate)
        {
            var doc = Load(characterFolder);
            if (doc.Sequences.RemoveAll(s => s.Id == id) == 0) return;
            var path = PathFor(characterFolder);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
