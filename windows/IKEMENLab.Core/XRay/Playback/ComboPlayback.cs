using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

/// <summary>What one playback run is.</summary>
public enum PlaybackMode
{
    /// <summary>A candidate combo route, judged by the route verifier (Verified / Failed / Inconclusive).</summary>
    Combo,
    /// <summary>One ability from neutral through its own command (a one-step route); the verifier's step check says whether it was performed.</summary>
    Ability,
    /// <summary>A state forced with the engine's changeState for a look. Never proof, never a verdict.</summary>
    Preview,
    /// <summary>A Sequence Lab sequence (one or more trials, in the isolated experiment store).</summary>
    Sequence
}

/// <summary>Anything the playback session can run. Every job has a scope: its result is only ever shown against that scope.</summary>
public interface IPlaybackJob
{
    PlaybackMode Mode { get; }
    /// <summary>A combo's route key; <c>ability-play:…</c> for Play Ability; <c>preview:…</c> for State Preview. The three never collide.</summary>
    string ScopeKey { get; }
    PlaybackSetup Setup { get; }
    StaticSnapshot? Snapshot { get; }
    string Summary { get; }
}

public sealed record PlaybackRequest(
    string Root, string SubjectFolder, string SubjectDef, CandidateGraph Graph, ComboRoute Route, PlaybackSetup Setup, string RouteSummary) : IPlaybackJob
{
    /// <summary>The route's static evidence as it was when the attempt started. Captured once per attempt; later edits or re-indexing never change it.</summary>
    public StaticSnapshot? Snapshot { get; init; }

    /// <summary>
    /// <see cref="PlaybackMode.Combo"/> (default) or <see cref="PlaybackMode.Ability"/>. For an ability, <see cref="Route"/> is the one-step route from
    /// <see cref="AbilityPlayback.Resolve"/> and <see cref="AbilityId"/> names the ability.
    /// </summary>
    public PlaybackMode Mode { get; init; } = PlaybackMode.Combo;
    public string? AbilityId { get; init; }

    public string ScopeKey => Mode == PlaybackMode.Ability ? AbilityPlayback.ScopeKey(AbilityId ?? Route.Key) : Route.Key;
    public string Summary => RouteSummary;
}

/// <summary>A State Preview of <see cref="StateId"/> (not proof).</summary>
public sealed record PreviewRequest(
    string Root, string SubjectFolder, string SubjectDef, SemanticIndex Index, string StateId, PlaybackSetup Setup, string Summary) : IPlaybackJob
{
    public StaticSnapshot? Snapshot { get; init; }
    public PlaybackMode Mode => PlaybackMode.Preview;
    public string ScopeKey => StatePreview.ScopeKey(StateId);
}

/// <summary>The saved result of one playback. Everything else (plan, report, trace) lives beside it in <see cref="Directory"/>.</summary>
public sealed record PlaybackRecord(
    string Id, DateTime CreatedUtc, string Character, string RouteKey, string RouteSummary, string Status, string? Reason, int? FailedStep,
    string? Dummy, string? Stage, string? EngineSha256, double Seconds, string Directory)
{
    public string TracePath => Path.Combine(Directory, "trace.jsonl");
    public string ReportPath => Path.Combine(Directory, "report.json");
    public string PlanPath => Path.Combine(Directory, "plan.json");
    public string DiagnosticPath => Path.Combine(Directory, "diagnostic.json");
    /// <summary>The approach-distance threshold the plan used (a configured value, not a measured range).</summary>
    public int ApproachDistance { get; init; } = PlaybackPreflight.DefaultApproachDistance;
    /// <summary>"combo", "ability" or "preview". Status is the route verdict, the ability result, or the preview status respectively.</summary>
    public string Mode { get; init; } = "combo";
    /// <summary>The ability (Play Ability) or state (State Preview) the run was for; null for a combo.</summary>
    public string? TargetId { get; init; }
    public string AbilityReportPath => System.IO.Path.Combine(Directory, "ability.json");
    public string PreviewReportPath => System.IO.Path.Combine(Directory, "preview.json");
}

public sealed record PlaybackOutcome(PlaybackRecord Record, VerificationReport Report, TraceLog Log)
{
    /// <summary>The exact plan that was played.</summary>
    public InputPlan? Plan { get; init; }
    public StaticSnapshot? Snapshot { get; init; }
    /// <summary>Play Ability only: the ability result read from <see cref="Report"/> (the route verifier's report on the one-step plan).</summary>
    public AbilityReport? Ability { get; init; }
    public FailureInspection? Failure => Ability is { } a ? AbilityVerifier.Inspect(a, Report, Log) : PlaybackInspector.Inspect(Report, Log);
}

/// <summary>A Sequence Lab run (×N trials of one sequence version) as a session job. Its scope is the exact sequence version.</summary>
public sealed record SequenceJob(string SequenceKey, string Label, PlaybackSetup Setup, int Trials, string Summary) : IPlaybackJob
{
    public StaticSnapshot? Snapshot { get; init; }
    public PlaybackMode Mode => PlaybackMode.Sequence;
    public string ScopeKey => Sequences.Sequence.ScopePrefix + SequenceKey;
}

/// <summary>One run of a sequence plan (Sequence Lab). The plan is built once per experiment by <see cref="Sequences.SequencePlanner"/>.</summary>
public sealed record SequenceRunRequest(string Root, string SubjectFolder, string SubjectDef, InputPlan Plan, IReadOnlyList<string> Labels, PlaybackSetup Setup, string Summary);

/// <summary>The saved result of one sequence trial: the sequence verdict, per-step results and the trace.</summary>
public sealed record SequenceOutcome(PlaybackRecord Record, Sequences.SequenceReport Report, TraceLog Log)
{
    public InputPlan? Plan { get; init; }
}

/// <summary>The saved result of one State Preview. There is no verification report: a preview is never judged.</summary>
public sealed record PreviewOutcome(PlaybackRecord Record, PreviewReport Report, TraceLog Log)
{
    public InputPlan? Plan { get; init; }
    public StaticSnapshot? Snapshot { get; init; }
}

/// <summary>
/// Plays one candidate route visibly: the same planner, driver, sandbox and verifier as the CLI's runtime-verify, with the engine window
/// left on screen, and the plan, trace and verdict kept so the user can look at them afterwards. No second playback architecture.
/// </summary>
public sealed class ComboPlaybackService
{
    /// <summary>Ticks the engine stays up on the final position (≈ 1.5 s) so the result can be seen before the window closes.</summary>
    public const int LingerFrames = 90;
    public const int KeepRecords = 25;

    /// <summary>How many records this store keeps (newest first). The playback store keeps <see cref="KeepRecords"/>; an experiment's own trial store keeps all of its trials.</summary>
    public int RecordLimit { get; init; } = KeepRecords;

    private readonly IEngineRunner _runner;
    private readonly string _storeRoot;
    private readonly string? _sandboxBase;

    public ComboPlaybackService(IEngineRunner? runner = null, string? storeRoot = null, string? sandboxBase = null)
    {
        _sandboxBase = sandboxBase;
        _runner = runner ?? new ProcessEngineRunner();
        _storeRoot = storeRoot ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "xray-playback");
        // A real engine takes the machine-wide one-engine lease; a test runner that launches nothing does not.
        Broker = _runner is ProcessEngineRunner ? RuntimeBroker.Shared : null;
    }

    public string StoreRoot => _storeRoot;

    /// <summary>
    /// The one-engine broker a job owner (the app's <see cref="PlaybackSession"/>, a CLI command, the MCP job queue) takes a lease from for the whole
    /// job before anything here launches. <see cref="RuntimeBroker.Shared"/> for the real engine runner; null (no lease) for test runners.
    /// This service itself never takes the lease: an experiment's trials run under the lease of the job that started them.
    /// </summary>
    public RuntimeBroker? Broker { get; init; }

    /// <summary>
    /// The same engine runner, sandbox base and seams, writing to another store with its own record limit. Sequence experiments use this so their
    /// trials live in the isolated experiment store and can never evict the Play Combo / Play Ability history. Cleanup failures are reported here too.
    /// </summary>
    public ComboPlaybackService WithStore(string storeRoot, int recordLimit)
    {
        var child = new ComboPlaybackService(_runner, storeRoot, _sandboxBase) { RecordLimit = recordLimit, CommitSeam = CommitSeam, SandboxDeleter = SandboxDeleter, Broker = Broker };
        child.CleanupFailed += (path, why) => CleanupFailed?.Invoke(path, why);
        return child;
    }

    /// <summary>Plays the route and returns the saved outcome. Throws InvalidOperationException (route cannot be scripted / setup incomplete) or OperationCanceledException.</summary>
    public PlaybackOutcome Play(PlaybackRequest request, Action<string>? progress = null, CancellationToken cancel = default)
    {
        using var gate = new PlaybackCancellation(cancel);
        return Play(request, progress, gate);
    }

    /// <summary>Test seam: called with "before-commit" (after the last pre-commit check, before the atomic commit) and "after-commit". Null in production.</summary>
    public Action<string>? CommitSeam { get; init; }

    /// <summary>Test seam: replaces sandbox deletion (returns (false, reason) to simulate a folder that cannot be removed). Null in production.</summary>
    public Func<string, (bool Ok, string? Why)>? SandboxDeleter { get; init; }

    /// <summary>Raised when a sandbox could not be deleted (path, reason). The folder carries the sandbox marker and is safe to delete by hand.</summary>
    public event Action<string, string?>? CleanupFailed;

    /// <summary>
    /// Plays the route. Cancellation and result commit share one atomic decision (<see cref="PlaybackCancellation"/>): either the cancel is
    /// accepted first and nothing is published, or the commit wins and the record survives a later cancel.
    /// </summary>
    public PlaybackOutcome Play(PlaybackRequest request, Action<string>? progress, PlaybackCancellation gate)
    {
        var cancel = gate.Token;
        var setup = request.Setup;
        if (!setup.Ready) throw new InvalidOperationException("Playback is not set up: " + string.Join(" ", setup.Issues));
        var ability = request.Mode == PlaybackMode.Ability;
        if (ability && request.AbilityId is null) throw new InvalidOperationException("Play Ability needs the ability it plays.");
        if (request.Mode is PlaybackMode.Preview or PlaybackMode.Sequence) throw new InvalidOperationException("A preview or a sequence is never played as a route.");

        var started = DateTime.UtcNow;
        var id = started.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(_storeRoot, id);
        Directory.CreateDirectory(dir);
        // Play Ability watches longer after its one step so recovery and the move's effect are recorded; the plan is otherwise the route plan.
        var planOptions = ability
            ? AbilityPlayback.PlanOptionsFor(request.Graph, request.Route, setup.ApproachDistance)
            : new PlanOptions { ApproachDistance = setup.ApproachDistance };
        var snapshot = request.Snapshot ?? StaticSnapshot.Capture(request.Graph, request.Route, request.AbilityId);

        var sandbox = new SandboxRequest(request.Root, request.SubjectFolder, request.SubjectDef, setup.Dummy!, setup.DummyDef!, setup.Stage!, planOptions.MaxFrames, _sandboxBase,
            AdapterPath: null, EngineExePath: setup.EnginePath, EngineRuntimeDlls: setup.EngineDlls) { LingerFrames = LingerFrames, Deleter = SandboxDeleter };
        var run = new VerifyRunRequest(sandbox, TimeSpan.FromSeconds(180))
        {
            Progress = progress,
            Cancel = cancel,
            CleanupFailed = (path, why) => CleanupFailed?.Invoke(path, why),
            Collect = a =>
            {
                File.WriteAllText(Path.Combine(dir, "plan.json"), InputPlanner.ToJson(a.Plan), new UTF8Encoding(false));
                if (a.RawTrace is not null) File.WriteAllText(Path.Combine(dir, "trace.jsonl"), a.RawTrace, new UTF8Encoding(false));
            }
        };

        // Everything before the commit is uncommitted: a cancel (or any failure) deletes the folder and yields no result. The commit is one
        // atomic transition shared with Cancel(): meta.json (what makes a record visible) only appears inside it.
        VerifyRunResult result;
        PlaybackRecord record;
        AbilityReport? abilityReport = null;
        var tmp = Path.Combine(dir, "meta.json.tmp");
        try
        {
            result = VerifyRunner.Run(request.Graph, request.Route, run, _runner, planOptions);
            cancel.ThrowIfCancellationRequested();
            var report0 = result.Report;
            File.WriteAllText(Path.Combine(dir, "report.json"), RouteVerifier.ToJson(report0), new UTF8Encoding(false));
            if (ability)
            {
                // The ability result is read from the route verifier's own report on the one-step plan; it is saved beside report.json.
                abilityReport = AbilityVerifier.Judge(request.AbilityId!, result.Plan!, result.Log ?? new TraceLog { Events = [], Issues = [] }, report0);
                File.WriteAllText(Path.Combine(dir, "ability.json"), AbilityVerifier.ToJson(abilityReport), new UTF8Encoding(false));
            }

            record = new PlaybackRecord(id, started, report0.Character, report0.RouteKey, request.RouteSummary,
                abilityReport?.Status.ToString() ?? report0.Status.ToString(), abilityReport is null ? report0.Reason : abilityReport.Reason,
                abilityReport is null ? report0.FailedStep : abilityReport.Status == AbilityStatus.Performed ? null : 1,
                setup.Dummy, setup.Stage, report0.EngineSha256, Math.Round((DateTime.UtcNow - started).TotalSeconds, 1), dir)
            {
                ApproachDistance = planOptions.ApproachDistance, Mode = ability ? "ability" : "combo", TargetId = request.AbilityId
            };
            Commit(dir, tmp, record, gate, cancel);
        }
        catch
        {
            TryDelete(dir);   // cancelled, refused or crashed before the commit: no half-written record is left behind
            throw;
        }

        CommitSeam?.Invoke("after-commit");
        var report = result.Report;
        Prune();

        var log = File.Exists(record.TracePath) ? TraceReader.ReadFile(record.TracePath) : result.Log ?? new TraceLog { Events = [], Issues = [] };
        return new PlaybackOutcome(record, report, log) { Plan = result.Plan, Snapshot = snapshot, Ability = abilityReport };
    }

    /// <summary>Writes the temp meta and publishes it inside the one atomic commit shared with Cancel. Throws when the cancel won.</summary>
    private void Commit(string dir, string tmp, PlaybackRecord record, PlaybackCancellation gate, CancellationToken cancel)
    {
        File.WriteAllText(tmp, MetaJson(record), new UTF8Encoding(false));
        CommitSeam?.Invoke("before-commit");
        if (!gate.TryCommit(() => File.Move(tmp, Path.Combine(dir, "meta.json"))))
            throw new OperationCanceledException(cancel);
    }

    /// <summary>
    /// Forces a state for a look (State Preview) in the same disposable sandbox, driver, probe, record store and commit/cancel gate as a route.
    /// Nothing is judged: the trace is read into a <see cref="PreviewReport"/>, which is never a verdict and never proof.
    /// </summary>
    public PreviewOutcome Preview(PreviewRequest request, Action<string>? progress, PlaybackCancellation gate)
    {
        var cancel = gate.Token;
        var setup = request.Setup;
        if (!setup.Ready) throw new InvalidOperationException("Playback is not set up: " + string.Join(" ", setup.Issues));

        progress?.Invoke("planning");
        cancel.ThrowIfCancellationRequested();
        var tail = AbilityPlayback.TailFramesFor(StatePreview.AnimTicks(request.Index, request.StateId));
        var planned = StatePreview.Plan(request.Index, request.StateId, setup.ApproachDistance, tail);
        if (planned.Plan is null) throw new InvalidOperationException(planned.RefusedReason);
        var plan = planned.Plan;

        var started = DateTime.UtcNow;
        var id = started.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(_storeRoot, id);
        Directory.CreateDirectory(dir);
        var snapshot = request.Snapshot ?? StaticSnapshot.CaptureState(request.Index, request.StateId);
        var sandbox = new SandboxRequest(request.Root, request.SubjectFolder, request.SubjectDef, setup.Dummy!, setup.DummyDef!, setup.Stage!, plan.MaxFrames, _sandboxBase,
            AdapterPath: null, EngineExePath: setup.EnginePath, EngineRuntimeDlls: setup.EngineDlls) { LingerFrames = LingerFrames, Deleter = SandboxDeleter };
        var run = new VerifyRunRequest(sandbox, TimeSpan.FromSeconds(180))
        {
            Progress = progress,
            Cancel = cancel,
            CleanupFailed = (path, why) => CleanupFailed?.Invoke(path, why),
            Collect = a =>
            {
                File.WriteAllText(Path.Combine(dir, "plan.json"), InputPlanner.ToJson(a.Plan), new UTF8Encoding(false));
                if (a.RawTrace is not null) File.WriteAllText(Path.Combine(dir, "trace.jsonl"), a.RawTrace, new UTF8Encoding(false));
            }
        };

        EngineRun engineRun;
        PreviewReport report;
        PlaybackRecord record;
        var tmp = Path.Combine(dir, "meta.json.tmp");
        try
        {
            engineRun = VerifyRunner.Execute(plan, run, _runner);
            cancel.ThrowIfCancellationRequested();
            report = StatePreview.Read(plan, engineRun.Log);
            report = report with { RunNotes = engineRun.Notes };
            File.WriteAllText(Path.Combine(dir, "preview.json"), StatePreview.ToJson(report), new UTF8Encoding(false));
            record = new PlaybackRecord(id, started, plan.Character, plan.RouteKey, request.Summary, report.Status.ToString(), report.Reason, null,
                setup.Dummy, setup.Stage, report.EngineSha256, Math.Round((DateTime.UtcNow - started).TotalSeconds, 1), dir)
            {
                ApproachDistance = plan.ApproachDistance, Mode = "preview", TargetId = request.StateId
            };
            Commit(dir, tmp, record, gate, cancel);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }

        CommitSeam?.Invoke("after-commit");
        Prune();
        var log = File.Exists(record.TracePath) ? TraceReader.ReadFile(record.TracePath) : engineRun.Log;
        return new PreviewOutcome(record, report, log) { Plan = plan, Snapshot = snapshot };
    }

    /// <summary>Saved records, newest first. Unreadable folders are skipped.</summary>
    public IReadOnlyList<PlaybackRecord> List()
    {
        var list = new List<PlaybackRecord>();
        if (!Directory.Exists(_storeRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(_storeRoot))
        {
            var meta = Path.Combine(dir, "meta.json");
            if (!File.Exists(meta)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                var r = doc.RootElement;
                string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                list.Add(new PlaybackRecord(S("id") ?? Path.GetFileName(dir), DateTime.TryParse(S("createdUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : default,
                    S("character") ?? "", S("route") ?? "", S("summary") ?? "", S("status") ?? "", S("reason"),
                    r.TryGetProperty("failedStep", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt32() : null,
                    S("dummy"), S("stage"), S("engineSha256"), r.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : 0, dir)
                {
                    ApproachDistance = r.TryGetProperty("approachDistance", out var ad) && ad.ValueKind == JsonValueKind.Number ? ad.GetInt32() : PlaybackPreflight.DefaultApproachDistance,
                    Mode = S("mode") ?? "combo",
                    TargetId = S("target")
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* skip */ }
        }

        return list.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    private void Prune()
    {
        foreach (var old in List().Skip(RecordLimit)) TryDelete(old.Directory);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>
    /// Plays one trial of a sequence plan in the same disposable sandbox, driver, probe and commit/cancel gate as a route, and judges it with the
    /// <see cref="Sequences.SequenceVerifier"/>. The record goes to this service's store (the experiment store, for the Sequence Lab).
    /// </summary>
    public SequenceOutcome PlaySequence(SequenceRunRequest request, Action<string>? progress, PlaybackCancellation gate)
    {
        var cancel = gate.Token;
        var setup = request.Setup;
        if (!setup.Ready) throw new InvalidOperationException("Playback is not set up: " + string.Join(" ", setup.Issues));
        var plan = request.Plan;
        progress?.Invoke("planning");
        cancel.ThrowIfCancellationRequested();

        var started = DateTime.UtcNow;
        var id = started.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(_storeRoot, id);
        Directory.CreateDirectory(dir);
        var sandbox = new SandboxRequest(request.Root, request.SubjectFolder, request.SubjectDef, setup.Dummy!, setup.DummyDef!, setup.Stage!, plan.MaxFrames, _sandboxBase,
            AdapterPath: null, EngineExePath: setup.EnginePath, EngineRuntimeDlls: setup.EngineDlls) { LingerFrames = LingerFrames, Deleter = SandboxDeleter };
        var run = new VerifyRunRequest(sandbox, TimeSpan.FromSeconds(Math.Max(180, plan.MaxFrames / 60 + 120)))
        {
            Progress = progress,
            Cancel = cancel,
            CleanupFailed = (path, why) => CleanupFailed?.Invoke(path, why),
            Collect = a =>
            {
                File.WriteAllText(Path.Combine(dir, "plan.json"), InputPlanner.ToJson(a.Plan), new UTF8Encoding(false));
                if (a.RawTrace is not null) File.WriteAllText(Path.Combine(dir, "trace.jsonl"), a.RawTrace, new UTF8Encoding(false));
            }
        };

        EngineRun engineRun;
        Sequences.SequenceReport report;
        PlaybackRecord record;
        var tmp = Path.Combine(dir, "meta.json.tmp");
        try
        {
            engineRun = VerifyRunner.Execute(plan, run, _runner);
            cancel.ThrowIfCancellationRequested();
            report = Sequences.SequenceVerifier.Verify(plan, engineRun.Log, request.Labels);
            report = report with { Notes = report.Notes.Concat(engineRun.Notes).ToList() };
            File.WriteAllText(Path.Combine(dir, "sequence.json"), Sequences.SequenceVerifier.ToJson(report), new UTF8Encoding(false));
            record = new PlaybackRecord(id, started, plan.Character, plan.RouteKey, request.Summary, report.Verdict.ToString(), report.Reason, report.FailedStep,
                setup.Dummy, setup.Stage, report.EngineSha256, Math.Round((DateTime.UtcNow - started).TotalSeconds, 1), dir)
            {
                ApproachDistance = plan.ApproachDistance, Mode = "sequence", TargetId = plan.RouteKey
            };
            Commit(dir, tmp, record, gate, cancel);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }

        CommitSeam?.Invoke("after-commit");
        Prune();
        var log = File.Exists(record.TracePath) ? TraceReader.ReadFile(record.TracePath) : engineRun.Log;
        return new SequenceOutcome(record, report, log) { Plan = plan };
    }

    private static string MetaJson(PlaybackRecord r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "ikemenlab.xray.playback/1");
            w.WriteString("id", r.Id);
            w.WriteString("createdUtc", r.CreatedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("character", r.Character);
            w.WriteString("route", r.RouteKey);
            w.WriteString("summary", r.RouteSummary);
            w.WriteString("status", r.Status);
            if (r.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", r.Reason);
            if (r.FailedStep is { } f) w.WriteNumber("failedStep", f); else w.WriteNull("failedStep");
            if (r.Dummy is null) w.WriteNull("dummy"); else w.WriteString("dummy", r.Dummy);
            if (r.Stage is null) w.WriteNull("stage"); else w.WriteString("stage", r.Stage);
            if (r.EngineSha256 is null) w.WriteNull("engineSha256"); else w.WriteString("engineSha256", r.EngineSha256);
            w.WriteNumber("seconds", r.Seconds);
            w.WriteNumber("approachDistance", r.ApproachDistance);
            if (r.Mode != "combo")
            {
                w.WriteString("mode", r.Mode);
                if (r.TargetId is null) w.WriteNull("target"); else w.WriteString("target", r.TargetId);
                if (r.Mode == "preview") w.WriteBoolean("proof", false);
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
