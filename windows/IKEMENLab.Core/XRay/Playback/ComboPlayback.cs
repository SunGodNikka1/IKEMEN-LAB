using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

public sealed record PlaybackRequest(
    string Root, string SubjectFolder, string SubjectDef, CandidateGraph Graph, ComboRoute Route, PlaybackSetup Setup, string RouteSummary);

/// <summary>The saved result of one playback. Everything else (plan, report, trace) lives beside it in <see cref="Directory"/>.</summary>
public sealed record PlaybackRecord(
    string Id, DateTime CreatedUtc, string Character, string RouteKey, string RouteSummary, string Status, string? Reason, int? FailedStep,
    string? Dummy, string? Stage, string? EngineSha256, double Seconds, string Directory)
{
    public string TracePath => Path.Combine(Directory, "trace.jsonl");
    public string ReportPath => Path.Combine(Directory, "report.json");
    public string PlanPath => Path.Combine(Directory, "plan.json");
}

public sealed record PlaybackOutcome(PlaybackRecord Record, VerificationReport Report, TraceLog Log)
{
    public FailureInspection? Failure => PlaybackInspector.Inspect(Report, Log);
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

    private readonly IEngineRunner _runner;
    private readonly string _storeRoot;
    private readonly string? _sandboxBase;

    public ComboPlaybackService(IEngineRunner? runner = null, string? storeRoot = null, string? sandboxBase = null)
    {
        _sandboxBase = sandboxBase;
        _runner = runner ?? new ProcessEngineRunner();
        _storeRoot = storeRoot ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "xray-playback");
    }

    public string StoreRoot => _storeRoot;

    /// <summary>Plays the route and returns the saved outcome. Throws InvalidOperationException (route cannot be scripted / setup incomplete) or OperationCanceledException.</summary>
    public PlaybackOutcome Play(PlaybackRequest request, Action<string>? progress = null, CancellationToken cancel = default)
    {
        var setup = request.Setup;
        if (!setup.Ready) throw new InvalidOperationException("Playback is not set up: " + string.Join(" ", setup.Issues));

        var started = DateTime.UtcNow;
        var id = started.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(_storeRoot, id);
        Directory.CreateDirectory(dir);
        var planOptions = new PlanOptions();

        var sandbox = new SandboxRequest(request.Root, request.SubjectFolder, request.SubjectDef, setup.Dummy!, setup.DummyDef!, setup.Stage!, planOptions.MaxFrames, _sandboxBase,
            AdapterPath: null, EngineExePath: setup.EnginePath, EngineRuntimeDlls: setup.EngineDlls) { LingerFrames = LingerFrames };
        var run = new VerifyRunRequest(sandbox, TimeSpan.FromSeconds(180))
        {
            Progress = progress,
            Cancel = cancel,
            Collect = a =>
            {
                File.WriteAllText(Path.Combine(dir, "plan.json"), InputPlanner.ToJson(a.Plan), new UTF8Encoding(false));
                if (a.RawTrace is not null) File.WriteAllText(Path.Combine(dir, "trace.jsonl"), a.RawTrace, new UTF8Encoding(false));
            }
        };

        // Everything up to and including meta.json is the uncommitted part: a cancel (or any failure) until then deletes the folder and
        // yields no result. Writing meta.json is the commit; a cancel after it no longer takes the result back.
        VerifyRunResult result;
        PlaybackRecord record;
        try
        {
            result = VerifyRunner.Run(request.Graph, request.Route, run, _runner, planOptions);
            cancel.ThrowIfCancellationRequested();
            var report0 = result.Report;
            File.WriteAllText(Path.Combine(dir, "report.json"), RouteVerifier.ToJson(report0), new UTF8Encoding(false));
            record = new PlaybackRecord(id, started, report0.Character, report0.RouteKey, request.RouteSummary, report0.Status.ToString(), report0.Reason, report0.FailedStep,
                setup.Dummy, setup.Stage, report0.EngineSha256, Math.Round((DateTime.UtcNow - started).TotalSeconds, 1), dir);
            cancel.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(dir, "meta.json"), MetaJson(record), new UTF8Encoding(false));
        }
        catch
        {
            TryDelete(dir);   // cancelled, refused or crashed: no half-written record is left behind
            throw;
        }

        var report = result.Report;
        Prune();

        var log = File.Exists(record.TracePath) ? TraceReader.ReadFile(record.TracePath) : result.Log ?? new TraceLog { Events = [], Issues = [] };
        return new PlaybackOutcome(record, report, log);
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
                    S("dummy"), S("stage"), S("engineSha256"), r.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : 0, dir));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* skip */ }
        }

        return list.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    private void Prune()
    {
        foreach (var old in List().Skip(KeepRecords)) TryDelete(old.Directory);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
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
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
