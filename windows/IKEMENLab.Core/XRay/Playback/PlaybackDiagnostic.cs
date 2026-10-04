using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

public sealed record DiagAttempt(int Id, string State, string? Issue, string? RouteKey);
public sealed record DiagResult(string? RunId, int? AttemptId, string? RouteKey, string? Verdict, string? Reason, int? FailedStep, string? Headline);
public sealed record DiagCharacter(string Id, string Folder, string Def, IReadOnlyList<SnapshotFile> Files);
public sealed record DiagRouteStep(int Index, string Edge, string Kind, string From, string To, string? Command);
public sealed record DiagPlannedInput(int Step, string Command, IReadOnlyList<string> Frames);
public sealed record DiagReportedInput(long Frame, int? Step, string? Phase, IReadOnlyList<string> Keys);
public sealed record DiagTelemetry(
    long Frame, int? State, bool? Ctrl, string? StateType, string? MoveType, double? Power, int? Facing, double? X, double? Y, double? DerivedDistance, string? DistanceSource);
public sealed record DiagWindowRow(
    long Frame, int? P1State, string? P1MoveType, bool? P1Ctrl, int? P1MoveHit, int? P1MoveContact, int? P2State, string? P2MoveType, double? P2Life,
    double? P1X, double? P2X, double? DerivedDistance, string? DistanceSource, string Notes);
public sealed record DiagSpacing(
    int ConfiguredApproachDistance, string ConfiguredMeaning, double? DerivedSeparationAtInput, string? MeasuredWindow, double? DerivedSeparationAtWindowStart,
    double? DerivedSeparationAtWindowEnd, double? DerivedSeparationMin, double? DerivedSeparationMax, IReadOnlyList<string> DistanceSources, string Note);
/// <summary>What the verifier recorded for one route step: where its input went out, where contact was seen, where the transition was observed.</summary>
public sealed record DiagStepOutcome(int Index, string Edge, string Outcome, string? Reason, long? InputFrame, long? ContactFrame, long? TransitionFrame);
public sealed record DiagRuntime(
    string? FailureReason, StepEvidence? Failure, string? Narrative, IReadOnlyList<DiagReportedInput> DriverReportedInputs, long? FailureAnchorFrame,
    DiagTelemetry? PreInputTelemetry, DiagTelemetry? SourceOccurrenceStartTelemetry, DiagTelemetry? SourceOccurrenceEndTelemetry,
    DiagSpacing Spacing, long? DecisiveFrame, IReadOnlyList<DiagWindowRow> DecisiveWindow, IReadOnlyList<string> MissingTelemetry, IReadOnlyList<string> TraceIntegrityIssues,
    string ExecutedController, IReadOnlyList<DiagStepOutcome> StepOutcomes);
public sealed record DiagEngine(string? Executable, string? Sha256, string? Version, string? Source);
/// <summary>Play Ability: which ability, through which command path, and what the run showed. Status is null when the attempt produced no result.</summary>
public sealed record DiagAbility(
    string AbilityId, string EntryState, string Edge, string? Command, string? Status, string? Reason, string? Detail, long? InputFrame, long? EntryFrame,
    IReadOnlyList<string> RuntimeRules, string? RouteVerifierStatus, string? RouteVerifierReason, MoveObservation? Observed, IReadOnlyList<string> Notes);
/// <summary>State Preview: never proof, never a verdict. Status is null when the attempt produced no preview.</summary>
public sealed record DiagPreview(
    bool Proof, string NotProof, string State, string? Status, string? Reason, string? Detail, long? ForceFrame, long? FirstSeenFrame, MoveObservation? Observed,
    IReadOnlyList<string> MissingTelemetry, IReadOnlyList<string> Notes);
public sealed record DiagSetup(string? Dummy, string? Stage, int ApproachDistance, string? PlanFingerprint);

/// <summary>
/// One versioned snapshot of a Play attempt for copying into a bug report or chat. It belongs to exactly the attempt (and, when one ran, the run) named in it,
/// is assembled from that attempt's own stored snapshot, plan, report and trace, and renders to deterministic text and JSON.
/// </summary>
public sealed record PlaybackDiagnostic(
    string Schema, string Kind, DiagAttempt Attempt, DiagResult? Result, DiagCharacter? Character, IReadOnlyList<DiagRouteStep> Route,
    int? FailedStep, IReadOnlyList<StepStaticSnapshot> StaticSteps, IReadOnlyList<DiagPlannedInput> PlannedInput, DiagRuntime? Runtime, DiagEngine? Engine, DiagSetup Setup,
    IReadOnlyList<string> Limitations)
{
    public const string SchemaVersion = "ikemenlab.xray.diagnostic/1";
    public const int WindowRadius = 12;

    /// <summary>User names of the route's states, frozen at attempt start (null = none). The ids and numbers elsewhere stay the evidence.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SnapshotName>? Names { get; init; }

    /// <summary>"ability" or "preview"; null for a combo route (whose diagnostic is unchanged).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagAbility? Ability { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagPreview? Preview { get; init; }

    private string WithName(string id) => Names?.FirstOrDefault(n => n.Id == id) is { } n ? $"{id} \"{n.Name}\"" : id;

    /// <summary>What the attempt was: "run" (an engine ran and produced a verdict), "refused" (rejected before launch), "ended" (cancelled or failed without a verdict).</summary>
    public static class Kinds { public const string Run = "run", Refused = "refused", Ended = "ended", Preview = "preview"; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Deterministic JSON (property order = declaration order, LF newlines on every platform).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions).Replace("\r\n", "\n");

    // ------------------------------------------------------------------ building

    /// <summary>The inputs for one attempt. Everything else the diagnostic says is taken from these, never from live state.</summary>
    public sealed record Source(
        int AttemptId, AttemptState AttemptState, string? AttemptIssue, string? AttemptRouteKey, PlaybackSetup? Setup, PlaybackRequest? Request,
        StaticSnapshot? Snapshot, PlaybackOutcome? Outcome, int? ResultAttemptId)
    {
        /// <summary>The State Preview this attempt ran (null for a route or Play Ability).</summary>
        public PreviewRequest? PreviewRequest { get; init; }
        public PreviewOutcome? Preview { get; init; }
    }

    public static PlaybackDiagnostic Build(Source s)
    {
        // A preview attempt — including one refused before any job existed, known only by its scope — is never described as a route.
        if (s.PreviewRequest is not null || s.AttemptRouteKey?.StartsWith(StatePreview.ScopePrefix, StringComparison.Ordinal) == true) return BuildPreview(s);
        var attempt = new DiagAttempt(s.AttemptId, s.AttemptState.ToString(), s.AttemptIssue, s.AttemptRouteKey);
        var approach = (s.AttemptState == AttemptState.VerdictProduced && s.ResultAttemptId == s.AttemptId ? s.Outcome?.Plan?.ApproachDistance : null) ?? s.Setup?.ApproachDistance ?? PlaybackPreflight.DefaultApproachDistance;
        var ran = s.AttemptState == AttemptState.VerdictProduced && s.ResultAttemptId == s.AttemptId ? s.Outcome : null;
        var setup = new DiagSetup(s.Setup?.Dummy ?? ran?.Record.Dummy, s.Setup?.Stage ?? ran?.Record.Stage, approach, ran?.Report.PlanFingerprint);

        DiagCharacter? character = null;
        if (s.Snapshot is { } snap)
            character = new DiagCharacter(snap.CharacterId, s.Request?.SubjectFolder ?? string.Empty, s.Request?.SubjectDef ?? string.Empty, snap.Files);

        IReadOnlyList<DiagRouteStep> route = s.Snapshot?.Steps.Select(x => new DiagRouteStep(x.Index, x.EdgeId, x.Kind, x.From, x.To, x.Commands.Count == 0 ? null : string.Join("+", x.Commands))).ToList() ?? [];

        // A result that belongs to an earlier attempt is never attached: only an outcome produced by THIS attempt is used.
        var outcome = s.AttemptState == AttemptState.VerdictProduced && s.ResultAttemptId == s.AttemptId ? s.Outcome : null;
        var kind = outcome is not null ? Kinds.Run : s.AttemptState == AttemptState.PreflightRefused ? Kinds.Refused : Kinds.Ended;
        var limitations = new List<string>(StaticSnapshot.StandardLimitations);

        if (outcome is null)
        {
            var why = kind == Kinds.Refused ? "No runtime evidence exists: the attempt was refused before any engine launch." : "No runtime verdict exists for this attempt.";
            limitations.Insert(0, why);
            return new PlaybackDiagnostic(SchemaVersion, kind, attempt, null, character, route, null, s.Snapshot?.Steps ?? [], [], null, null, setup, limitations)
                { Names = SnapshotNames(s.Snapshot), Mode = ModeOf(s), Ability = AbilityOf(s.Request, null) };
        }

        var report = outcome.Report;
        var log = outcome.Log;
        var ability = outcome.Ability;
        // A performed ability blames no step: the route verifier's "failed step" for a one-step plan only means no combo followed it.
        var failedStep = ability is { Status: AbilityStatus.Performed } ? null : report.FailedStep;
        var failure = failedStep is { } fs ? report.Steps.FirstOrDefault(x => x.Index == fs) : null;
        var inspection = outcome.Failure;
        var result = ability is not null
            ? new DiagResult(outcome.Record.Id, s.ResultAttemptId, report.RouteKey, ability.Status.ToString(), ability.Reason, failedStep, AbilityText.Headline(ability, s.Snapshot is { } sn ? sn.NameOf : null))
            : new DiagResult(outcome.Record.Id, s.ResultAttemptId, report.RouteKey, report.Status.ToString(), report.Reason, report.FailedStep, inspection?.Headline ?? (report.Status == VerifyStatus.Verified ? "Verified" : null));

        var planned = outcome.Plan?.Steps.Select(p => new DiagPlannedInput(p.Index, p.Command ?? string.Empty, p.Input.Select(f => f.Keys.Count == 0 ? "·" : string.Join("+", f.Keys)).ToList())).ToList() ?? [];
        var reported = log.Events.OfType<InputEvent>().Where(e => e.Player == 1).Take(80).Select(e => new DiagReportedInput(e.Frame, e.Step, e.Phase, e.Keys)).ToList();

        var ev = failure?.Evidence;
        var focus = inspection?.FocusFrame;
        var frames = log.Frames.ToList();
        // Telemetry is labelled by what it is: the frame at the attempted input (null when no input was attempted), and the start / end of the source occurrence.
        DiagTelemetry? At(long? frame) => frame is { } f && frames.LastOrDefault(x => x.Frame <= f) is { } pf
            ? new DiagTelemetry(pf.Frame, pf.P1.State, pf.P1.Ctrl, pf.P1.StateType, pf.P1.MoveType, pf.P1.Power, pf.P1.Facing, pf.P1.PosX, pf.P1.PosY, pf.Distance, pf.DistanceSource)
            : null;
        var pre = At(ev?.InputAttemptFrame);
        var occStart = At(ev?.SourceOccurrenceStartFrame);
        var occEnd = At(ev?.SourceOccurrenceEndFrame);

        var window = focus is { } fr
            ? PlaybackInspector.Timeline(log).Where(r => r.Frame >= fr - WindowRadius && r.Frame <= fr + WindowRadius)
                .Select(r => new DiagWindowRow(r.Frame, r.P1State, r.P1MoveType, r.P1Ctrl, r.P1MoveHit, FrameOf(frames, r.Frame)?.P1.MoveContact, r.P2State, r.P2MoveType, r.P2Life, r.P1X, r.P2X, r.Distance, r.DistanceSource, r.Notes)).ToList()
            : [];

        var spacing = new DiagSpacing(approach, "The distance P1 walks to before the route starts. It is a threshold, not the attack's range.",
            ev?.SeparationAtInput, ev?.SeparationWindow, ev?.SeparationAtSourceStart, ev?.SeparationAtSourceEnd, ev?.SeparationMin, ev?.SeparationMax, ev?.SeparationSources ?? [],
            "Separation is derived telemetry (see its source). 'At input' exists only when an input was attempted; the window figures describe the source occurrence (or the attempt window) and are not input telemetry. None of it is the configured approach distance.");

        var runtime = new DiagRuntime(ability is null ? report.Reason : ability.Reason, ev, failure?.Detail, reported, ev?.FailureAnchorFrame, pre, occStart, occEnd, spacing, focus, window, MissingTelemetry(frames, log.Meta),
            log.Issues.Select(i => $"line {i.Line}: {i.Message}").ToList(), "unknown",
            report.Steps.Select(x => new DiagStepOutcome(x.Index, x.EdgeId, x.Outcome.ToString(), x.Reason, x.InputFrame, x.ContactFrame, x.TransitionFrame)).ToList());
        var engine = new DiagEngine(report.EngineExecutable, report.EngineSha256, report.EngineVersion, report.EngineSource);
        return new PlaybackDiagnostic(SchemaVersion, kind, attempt, result, character, route, failedStep, s.Snapshot?.Steps ?? [], planned, runtime, engine, setup, limitations)
            { Names = SnapshotNames(s.Snapshot), Mode = ModeOf(s), Ability = AbilityOf(s.Request, ability) };
    }

    private static string? ModeOf(Source s) =>
        s.Request?.Mode == PlaybackMode.Ability || s.AttemptRouteKey?.StartsWith(AbilityPlayback.ScopePrefix, StringComparison.Ordinal) == true ? "ability" : null;

    private static DiagAbility? AbilityOf(PlaybackRequest? request, AbilityReport? a)
    {
        if (request is not { Mode: PlaybackMode.Ability }) return null;
        var step = request.Route.Steps.FirstOrDefault();
        var command = step is null || step.Edge.Commands.Count == 0 ? null : string.Join("+", step.Edge.Commands);
        return new DiagAbility(request.AbilityId ?? string.Empty, step?.Move.StateId ?? string.Empty, step?.Edge.Id ?? string.Empty, command,
            a?.Status.ToString(), a?.Reason, a?.Detail, a?.InputFrame, a?.EntryFrame, a?.RuntimeRules ?? [], a?.RouteVerifierStatus, a?.RouteVerifierReason, a?.Observed, a?.Notes ?? []);
    }

    /// <summary>A State Preview attempt: the snapshot's files and names, the preview's observations, and no verdict of any kind.</summary>
    private static PlaybackDiagnostic BuildPreview(Source s)
    {
        var request = s.PreviewRequest;
        var stateId = request?.StateId ?? s.AttemptRouteKey![StatePreview.ScopePrefix.Length..];
        var attempt = new DiagAttempt(s.AttemptId, s.AttemptState.ToString(), s.AttemptIssue, s.AttemptRouteKey);
        var produced = s.AttemptState == AttemptState.PreviewProduced && s.ResultAttemptId == s.AttemptId ? s.Preview : null;
        var kind = produced is not null ? Kinds.Preview : s.AttemptState == AttemptState.PreflightRefused ? Kinds.Refused : Kinds.Ended;
        var character = s.Snapshot is { } snap ? new DiagCharacter(snap.CharacterId, request?.SubjectFolder ?? string.Empty, request?.SubjectDef ?? string.Empty, snap.Files) : null;
        var limitations = new List<string>(StaticSnapshot.PreviewLimitations);
        if (produced is null) limitations.Insert(0, kind == Kinds.Refused ? "No runtime record exists: the preview was refused before any engine launch." : "This preview attempt produced no record.");
        var setup = new DiagSetup(s.Setup?.Dummy ?? produced?.Record.Dummy, s.Setup?.Stage ?? produced?.Record.Stage,
            produced?.Plan?.ApproachDistance ?? s.Setup?.ApproachDistance ?? PlaybackPreflight.DefaultApproachDistance, produced?.Report.PlanFingerprint);
        var r = produced?.Report;
        var result = produced is null ? null : new DiagResult(produced.Record.Id, s.ResultAttemptId, produced.Record.RouteKey, null, r!.Reason, null,
            StatePreview.Headline(r, s.Snapshot is { } sn ? sn.NameOf : null));
        var frames = produced?.Log.Frames.ToList() ?? [];
        var preview = new DiagPreview(false, PreviewReport.NotProof, stateId, r?.Status.ToString(), r?.Reason, r?.Detail, r?.ForceFrame, r?.FirstSeenFrame, r?.Observed,
            produced is null ? [] : MissingTelemetry(frames, produced.Log.Meta), r?.Notes ?? []);
        var engine = r is null ? null : new DiagEngine(r.EngineExecutable, r.EngineSha256, r.EngineVersion, r.EngineSource);
        return new PlaybackDiagnostic(SchemaVersion, kind, attempt, result, character, [], null, [], [], null, engine, setup, limitations)
            { Names = SnapshotNames(s.Snapshot), Mode = "preview", Preview = preview };
    }

    private static FrameEvent? FrameOf(List<FrameEvent> frames, long frame) => frames.FirstOrDefault(f => f.Frame == frame);

    /// <summary>Telemetry fields that were never readable in the whole recording, plus capabilities the engine build reported as unsupported.</summary>
    private static IReadOnlyList<string> MissingTelemetry(List<FrameEvent> frames, TraceMeta? meta)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        if (frames.Count > 0)
        {
            void Check(string prefix, Func<FrameEvent, PlayerSample> pick)
            {
                void F(string name, Func<PlayerSample, object?> get) { if (frames.All(f => get(pick(f)) is null)) missing.Add($"{prefix}.{name}"); }
                F("state", p => p.State); F("ctrl", p => p.Ctrl); F("stateType", p => p.StateType); F("moveType", p => p.MoveType); F("anim", p => p.Anim);
                F("animElem", p => p.AnimElem); F("life", p => p.Life); F("power", p => p.Power); F("x", p => p.PosX); F("y", p => p.PosY);
                F("facing", p => p.Facing); F("moveHit", p => p.MoveHit); F("moveContact", p => p.MoveContact); F("hitPause", p => p.HitPause);
            }

            Check("p1", f => f.P1);
            Check("p2", f => f.P2);
            if (frames.All(f => f.Distance is null)) missing.Add("distance");
        }

        if (meta is not null)
            foreach (var kv in meta.Capabilities.Where(c => !c.Value)) missing.Add("unsupported:" + kv.Key);
        return missing.ToList();
    }

    // ------------------------------------------------------------------ text

    private static string F(double? v) => v is { } d ? d.ToString("0.##", CultureInfo.InvariantCulture) : "n/a";
    private static string F(long? v) => v is { } d ? d.ToString(CultureInfo.InvariantCulture) : "n/a";
    private static string F(bool? v) => v is { } b ? (b ? "yes" : "no") : "n/a";
    private static string F(string? v) => string.IsNullOrEmpty(v) ? "n/a" : v;

    private static IReadOnlyList<SnapshotName>? SnapshotNames(StaticSnapshot? snapshot) => snapshot is { Names.Count: > 0 } ? snapshot.Names : null;

    /// <summary>Readable rendering for pasting. Deterministic: the same diagnostic always renders the same text.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        void Gate(IReadOnlyList<string> m, IReadOnlyList<string> u, IReadOnlyList<SnapshotTrigger> all, IReadOnlyList<SnapshotGroup> g, string indent) => GateLines(sb, indent, m, u, all, g);
        L(Mode switch
        {
            "ability" => $"IKEMEN Lab Play Ability diagnostic ({Schema})",
            "preview" => $"IKEMEN Lab State Preview diagnostic — NOT PROOF ({Schema})",
            _ => $"IKEMEN Lab combo playback diagnostic ({Schema})"
        });
        L($"kind: {Kind}");
        L($"attempt {Attempt.Id}: {Attempt.State}" + (Attempt.Issue is { Length: > 0 } i ? $" — {i}" : string.Empty));
        L(Mode is null ? $"route key: {F(Attempt.RouteKey)}" : $"scope: {F(Attempt.RouteKey)}");
        if (Mode == "preview")
        {
            if (Result is { } pr) L($"run: {F(pr.RunId)} (produced by attempt {F((long?)pr.AttemptId)})");
            if (Preview is { } pv) PreviewLines(sb, pv);
            if (Result?.Headline is { Length: > 0 } ph) L($"summary: {ph}");
        }
        else if (Result is { } r)
        {
            L($"run: {F(r.RunId)} (produced by attempt {F((long?)r.AttemptId)})");
            L($"verdict: {F(r.Verdict)}" + (r.Reason is null ? string.Empty : $" / {r.Reason}") + (r.FailedStep is { } f ? $" at step {f}" : string.Empty));
            if (r.Headline is { Length: > 0 } h) L($"summary: {h}");
        }
        else L("no runtime verdict belongs to this attempt");

        if (Ability is { } ab) AbilityLines(sb, ab);

        if (Character is { } c)
        {
            L();
            L($"character: {c.Id}  folder: {F(c.Folder)}  def: {F(c.Def)}");
            foreach (var file in c.Files) L($"  {file.Role,-6} {file.Path}  content-hash {file.ContentHash}");
        }

        if (Names is { Count: > 0 })
        {
            L();
            L("names (user labels in effect when the attempt started; ids and state numbers remain the evidence):");
            foreach (var n in Names) L($"  {n.Id} = {n.Name}  (X-Ray name: {n.DefaultName}; {n.Source})");
        }

        if (Route.Count > 0)
        {
            L();
            L("route:");
            foreach (var step in Route) L($"  {step.Index}. {step.Kind} {WithName(step.From)} -> {WithName(step.To)}" + (step.Command is null ? string.Empty : $"  [{step.Command}]") + $"  {step.Edge}");
        }

        foreach (var e in StaticSteps)
        {
            L();
            L($"static step {e.Index}{(e.Index == FailedStep ? " (FAILED STEP)" : string.Empty)}: {e.Kind} {WithName(e.From)} -> {WithName(e.To)}" + (e.Commands.Count > 0 ? $"  [{string.Join("+", e.Commands)}]" : string.Empty));
            L($"  controller: {e.ControllerId}" + (e.ControllerName is { Length: > 0 } n ? $" ({n})" : string.Empty) + $"  branch {e.BranchNumber}  confidence {e.Confidence}  rules {string.Join(",", e.EvidenceRules)}");
            if (e.ControllerSource is { } cs) L($"  source: {cs.File}:{cs.StartLine}" + (cs.EndLine != cs.StartLine ? $"-{cs.EndLine}" : string.Empty));
            Gate(e.ModelledRequirements, e.Unmodelled, e.TriggerAll, e.TriggerGroups, "  ");
            L($"  statically related controllers sharing a command ({e.CompetingControllers.Count}{(e.CompetingControllersTruncated ? ", list truncated at " + StaticSnapshot.MaxCompeting : string.Empty)}):");
            foreach (var x in e.CompetingControllers)
            {
                L($"    - {x.ControllerId}" + (x.ControllerName is { Length: > 0 } xn ? $" ({xn})" : string.Empty) + $" -> {x.TargetId}  shares {string.Join(",", x.SharedCommands)}  {x.FileOrder}  confidence {x.Confidence}" + Loc(x.ControllerSource));
                Gate(x.ModelledRequirements, x.Unmodelled, x.TriggerAll, x.TriggerGroups, "      ");
            }
        }

        if (PlannedInput.Count > 0)
        {
            L();
            L("planned input (· = release):");
            foreach (var p in PlannedInput) L($"  step {p.Step} [{p.Command}]: {string.Join(" ", p.Frames)}");
        }

        if (Runtime is { } rt)
        {
            L();
            L("runtime evidence:");
            if (rt.Narrative is { Length: > 0 } nar) L("  " + nar);
            if (rt.Failure is { } ev)
            {
                var stepNo = FailedStep is { } fsn ? fsn.ToString(CultureInfo.InvariantCulture) : "?";
                L($"  failed step {stepNo}; failure: {F(ev.Failure)}");
                L($"  expected state: {F((long?)ev.ExpectedState)}  expected state ever observed (anywhere in the trace): {F(ev.ExpectedStateEverObserved)}" + (ev.ExpectedStateFirstFrame is { } ef ? $" (first at frame {ef})" : string.Empty));
                L($"  samples observed through frame {F(ev.ObservedThroughFrame)}; observation of the judged window complete: {F(ev.ObservationComplete)}" + (ev.ObservationComplete ? string.Empty : " — INCOMPLETE: absence of an event is not shown"));
                L($"  judged attempt window: frames {F(ev.JudgedAttemptStartFrame)}-{F(ev.JudgedAttemptEndFrame)}; expected state entered inside it at: {F(ev.ExpectedTargetFirstFrame)}; transition preceded the input: {F(ev.TransitionPrecededInput)}");
                L($"  first mismatch: state {F((long?)ev.FirstMismatchState)} at frame {F(ev.FirstMismatchFrame)}");
                L($"  source state observed: {F(ev.SourceStateObserved)}" + (ev.SourceOccurrenceStartFrame is { } sf ? $" (occurrence frames {sf}-{F(ev.SourceOccurrenceEndFrame)}; end: {F(ev.SourceOccurrenceEndKind)})" : string.Empty));
                L($"  required contact: {F(ev.RequiredContact)}  observed: {F(ev.RequiredContactObserved)}");
                L($"  timing: required earliest tick {F((long?)ev.RequiredEarliestTick)}; source tick at the attempted input {F((long?)ev.SourceTickAtInput)}; satisfied: {F(ev.TimingSatisfied)}");
                L(ev.InputAttempted
                    ? $"  Step {stepNo} input was attempted ({string.Join("+", ev.AttemptedKeys)} at frame {F(ev.InputAttemptFrame)})"
                    : $"  Step {stepNo} input was not attempted.");
                L($"  failure anchor: frame {F(ev.FailureAnchorFrame)} ({F(ev.FailureAnchorKind)})");
                L($"  driver reported: {F(ev.DriverClaim)}" + (ev.DriverClaimFrame is { } df ? $" (frame {df}); this is the driver's claim, not a measurement" : string.Empty));
            }
            else
            {
                // No failed-step context exists. That is not the same as "an input was not attempted": say which situation this is.
                L(Result?.Verdict == "Verified"
                    ? "  failed-step context: none — run Verified (see the recorded step outcomes and driver-reported inputs below)"
                    : Result?.Verdict == nameof(AbilityStatus.Performed)
                    ? "  failed-step context: none — the ability was performed (see the recorded step outcome and what followed above)"
                    : $"  failed-step context: none — no single step is blamed (verdict {F(Result?.Verdict)}{(Result?.Reason is { } rr ? " / " + rr : string.Empty)})");
            }

            if (rt.StepOutcomes.Count > 0)
            {
                L("  recorded step outcomes:");
                foreach (var o in rt.StepOutcomes)
                    L($"    step {o.Index}: {o.Outcome}" + (o.Reason is null ? string.Empty : $" ({o.Reason})") + $"  input frame {F(o.InputFrame)}  contact frame {F(o.ContactFrame)}  transition frame {F(o.TransitionFrame)}");
            }

            L($"  executed controller: {rt.ExecutedController} (the trace does not record it)");
            L("  driver-reported inputs: " + (rt.DriverReportedInputs.Count == 0 ? "none" : string.Join(", ", rt.DriverReportedInputs.Select(x => $"f{x.Frame} step {F((long?)x.Step)} {(x.Keys.Count == 0 ? "release" : string.Join("+", x.Keys))}"))));
            string Tel(DiagTelemetry t) => $"frame {t.Frame}: state {F((long?)t.State)} ctrl {F(t.Ctrl)} statetype {F(t.StateType)} movetype {F(t.MoveType)} power {F(t.Power)} facing {F((long?)t.Facing)} pos ({F(t.X)}, {F(t.Y)}) derived distance {F(t.DerivedDistance)} [{F(t.DistanceSource)}]";
            var attempted = rt.Failure?.InputAttempted;
            var stepLabel = FailedStep is { } fsl ? $"Step {fsl}" : "The step";
            if (rt.Failure is not null)
            {
                // Three different situations are never merged: not attempted, attempted-but-missing, and (above) no failed step at all.
                L("  telemetry at the attempted input: " + (attempted != true ? $"n/a — {stepLabel} input was not attempted"
                    : rt.PreInputTelemetry is { } t ? Tel(t) : $"unavailable / not recorded (input at frame {F(rt.Failure.InputAttemptFrame)})"));
                if (rt.SourceOccurrenceStartTelemetry is { } ts) L("  source occurrence start: " + Tel(ts));
                if (rt.SourceOccurrenceEndTelemetry is { } te) L("  source occurrence end: " + Tel(te));
            }

            L($"  configured approach distance: {rt.Spacing.ConfiguredApproachDistance} — {rt.Spacing.ConfiguredMeaning}");
            if (rt.Failure is not null)
            {
                L("  derived separation at the attempted input: " + (attempted != true ? $"n/a — {stepLabel} input was not attempted"
                    : rt.Spacing.DerivedSeparationAtInput is { } di ? F(di) : "unavailable / not recorded"));
                L($"  derived separation over the {F(rt.Spacing.MeasuredWindow)}: start {F(rt.Spacing.DerivedSeparationAtWindowStart)}, end {F(rt.Spacing.DerivedSeparationAtWindowEnd)}, min {F(rt.Spacing.DerivedSeparationMin)}, max {F(rt.Spacing.DerivedSeparationMax)} [{string.Join(",", rt.Spacing.DistanceSources)}]");
            }

            L($"  decisive frame: {F(rt.DecisiveFrame)}; window ±{WindowRadius}:");
            foreach (var w in rt.DecisiveWindow)
                L($"    f{w.Frame}{(w.Frame == rt.DecisiveFrame ? "*" : " ")} P1 {F((long?)w.P1State)}/{F(w.P1MoveType)} ctrl {F(w.P1Ctrl)} hit {F((long?)w.P1MoveHit)} contact {F((long?)w.P1MoveContact)} | P2 {F((long?)w.P2State)}/{F(w.P2MoveType)} life {F(w.P2Life)} | x {F(w.P1X)},{F(w.P2X)} dist {F(w.DerivedDistance)} | {w.Notes}");
            L("  missing telemetry: " + (rt.MissingTelemetry.Count == 0 ? "none" : string.Join(", ", rt.MissingTelemetry)));
            L("  trace integrity issues: " + (rt.TraceIntegrityIssues.Count == 0 ? "none" : string.Join("; ", rt.TraceIntegrityIssues)));
        }

        if (Engine is { } en)
        {
            L();
            L($"engine: {F(en.Executable)}");
            L($"  sha256 {F(en.Sha256)}  version {F(en.Version)}  source {F(en.Source)}");
        }

        L();
        L($"setup: dummy {F(Setup.Dummy)}  stage {F(Setup.Stage)}  approach distance {Setup.ApproachDistance}  plan fingerprint {F(Setup.PlanFingerprint)}");
        L();
        L("limits of this diagnostic:");
        foreach (var lim in Limitations) L("  - " + lim);
        return sb.ToString();
    }

    private string StateText(string stateId) => WithName(stateId);

    private void AbilityLines(StringBuilder sb, DiagAbility a)
    {
        void L(string s = "") => sb.Append(s).Append('\n');
        L();
        L($"ability: {WithName(a.AbilityId)} — entry {WithName(a.EntryState)} via [{F(a.Command)}]  {a.Edge}");
        if (a.Status is null) { L("ability result: none for this attempt"); return; }
        L($"ability result: {a.Status}" + (a.Reason is null ? string.Empty : $" / {a.Reason}") + $"  input frame {F(a.InputFrame)}  move started at frame {F(a.EntryFrame)}");
        if (a.Detail is { Length: > 0 } d) L($"  {d}");
        L("  evidence: " + (a.RuntimeRules.Count == 0 ? "none (the move was not shown to start)" : string.Join(",", a.RuntimeRules) + " — the route verifier's step check, in this run only"));
        L($"  route verifier on the one-step plan: {F(a.RouteVerifierStatus)}" + (a.RouteVerifierReason is null ? string.Empty : $" / {a.RouteVerifierReason}") +
          " (a combo verdict; a single move is not a combo, so it is not the ability's result)");
        if (a.Observed is { } o) ObservedLines(sb, o, "  ");
        foreach (var n in a.Notes) L("  note: " + n);
    }

    private void PreviewLines(StringBuilder sb, DiagPreview p)
    {
        void L(string s = "") => sb.Append(s).Append('\n');
        L("PREVIEW — NOT PROOF: " + p.NotProof);
        L($"previewed state: {WithName(p.State)}");
        L($"preview status: {F(p.Status)}" + (p.Reason is null ? string.Empty : $" / {p.Reason}") + " (not a verdict)");
        if (p.Detail is { Length: > 0 } d) L($"  {d}");
        L($"  forced at frame {F(p.ForceFrame)}; first sampled in the state at frame {F(p.FirstSeenFrame)}");
        if (p.Observed is { } o) ObservedLines(sb, o, "  ");
        L("  missing telemetry: " + (p.MissingTelemetry.Count == 0 ? "none" : string.Join(", ", p.MissingTelemetry)));
        foreach (var n in p.Notes) L("  note: " + n);
    }

    private void ObservedLines(StringBuilder sb, MoveObservation o, string indent)
    {
        void L(string s) => sb.Append(indent).Append(s).Append('\n');
        L($"after the move started (frames {o.StartFrame}-{o.ObservedThroughFrame}, {o.ObservedFrames} samples; watch complete: {F(o.ObservationComplete)}" +
          (o.ObservationComplete ? ")" : " — INCOMPLETE: absence of an event is not shown)"));
        L($"  connected: {o.Connected}" + (o.ConnectedBy.Count > 0 ? " — " + string.Join("; ", o.ConnectedBy) : string.Empty) + (o.ContactFrame is { } cf ? $" (first at frame {cf})" : string.Empty));
        L($"  own hit {F(o.OwnHit)}  own guarded {F(o.OwnGuarded)} | opponent hit {F(o.OpponentHit)}  guarded {F(o.OpponentGuarded)}  launched {F(o.OpponentAirborne)}  knocked down {F(o.OpponentKnockedDown)}  reaction frame {F(o.ReactionFrame)}");
        L($"  damage {F(o.Damage)} (opponent life {F(o.OpponentLifeBefore)} -> lowest {F(o.OpponentLifeLowest)})");
        L(o.FramesUntilControl is { } c ? $"  back in control {c} frames after the start (frame {F(o.ControlFrame)})" : "  not back in control while watched");
        L("  P1 states: " + string.Join(" -> ", o.P1States.Select(n => StateText("state:" + n.ToString(CultureInfo.InvariantCulture)))) + (o.P1StatesTruncated ? " -> ..." : string.Empty));
        L("  not readable: " + (o.Missing.Count == 0 ? "none" : string.Join(", ", o.Missing)));
        L("  these are measurements of this one run (this dummy, this spacing), not claims about the character");
    }

    private static void GateLines(StringBuilder sb, string indent, IReadOnlyList<string> modelled, IReadOnlyList<string> unmodelled, IReadOnlyList<SnapshotTrigger> all, IReadOnlyList<SnapshotGroup> groups)
    {
        sb.Append(indent).Append("modelled requirements: ").Append(modelled.Count == 0 ? "none" : string.Join("; ", modelled)).Append('\n');
        sb.Append(indent).Append("unmodelled expressions (as written): ").Append(unmodelled.Count == 0 ? "none" : string.Join(" | ", unmodelled)).Append('\n');
        foreach (var t in all) sb.Append(indent).Append("triggerall = ").Append(t.Text).Append(Loc(t.Source)).Append('\n');
        foreach (var g in groups)
            foreach (var t in g.Lines) sb.Append(indent).Append("trigger").Append(g.Number).Append(g.IsExpectedBranch ? "*" : " ").Append("= ").Append(t.Text).Append(Loc(t.Source)).Append('\n');
    }

    private static string Loc(SourceSpan? s) => s is null ? string.Empty : $"   [{s.File}:{s.StartLine}" + (s.EndLine != s.StartLine ? $"-{s.EndLine}" : string.Empty) + "]";
}
