using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

public enum PreviewStatus
{
    /// <summary>The engine accepted the forced state and P1 was sampled in it.</summary>
    Forced,
    /// <summary>The engine accepted the forced state, but P1 had already left it when sampled (it changes state on its first tick).</summary>
    ForcedNotSeen,
    /// <summary>The engine's changeState refused the state (it reported no such state for this character).</summary>
    Refused,
    /// <summary>The preview never got as far as forcing the state, or the recording cannot be trusted.</summary>
    Inconclusive
}

/// <summary>
/// The result of one State Preview: a state forced with the engine's own <c>changeState</c> in a disposable match, and what followed. It is
/// <b>never proof</b>: forcing skips the command, every trigger and any prerequisite, so it says nothing about whether the move can be performed
/// normally. It cites no evidence rule, carries no confidence, is never a verdict, and nothing in the product counts it as a result.
/// </summary>
public sealed record PreviewReport(
    string Character, string StateId, int State, PreviewStatus Status, string? Reason, string? Detail, long? ForceFrame, long? FirstSeenFrame,
    MoveObservation? Observed, IReadOnlyList<string> Notes)
{
    public const string SchemaVersion = "ikemenlab.xray.preview/1";
    /// <summary>Always false. Kept as a field so every reader of the JSON sees it.</summary>
    public bool Proof => false;
    public const string NotProof = "Preview — not proof. The state was forced with the engine's changeState, skipping its command, its triggers and any prerequisite. It does not show the move can be performed normally, and it is never counted as a result.";

    public string? PlanFingerprint { get; init; }
    public string? EngineSha256 { get; init; }
    public string? EngineExecutable { get; init; }
    public string? EngineVersion { get; init; }
    public string? EngineSource { get; init; }
    /// <summary>The engine run's own notes (sandbox, engine binary, trace hash, engine problems), kept apart from what the preview reader says.</summary>
    public IReadOnlyList<string> RunNotes { get; init; } = [];
}

/// <summary>State Preview: the forced-state plan, and a reader for its trace that produces observations, never a verdict.</summary>
public static class StatePreview
{
    public const string ForceRefused = "ForceRefused";
    public const string ForceNotAttempted = "ForceNotAttempted";

    /// <summary>The scope a preview result belongs to. Never equal to a combo route key or a Play Ability scope.</summary>
    public const string ScopePrefix = "preview:";
    public static string ScopeKey(string stateId) => ScopePrefix + stateId;

    /// <summary>The literal animation length of a state (its Statedef anim with a finite AIR action), or null.</summary>
    public static int? AnimTicks(SemanticIndex index, string stateId)
    {
        var anim = index.Outgoing(stateId, RelationKind.UsesAnim).FirstOrDefault(r => r.Confidence == Confidence.StaticProven);
        var obj = anim is null ? null : index.Get(anim.To);
        return obj is not null && obj.Prop("endsInfinite") != "true" && int.TryParse(obj.Prop("totalTicks"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    /// <summary>Only the character's own, defined, non-negative states can be forced: not -1/-2/-3, not engine common states, not undefined ones.</summary>
    public static bool CanPreview(SemanticIndex index, string stateId, out string? why)
    {
        why = null;
        var s = index.Get(stateId);
        if (s is null || s.Kind != ObjectKind.State) why = $"{stateId} is not a state.";
        else if (s.IsStub) why = $"{s.Name} is referenced but not defined, so there is nothing to force.";
        else if (!int.TryParse(s.Prop("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) why = $"{s.Name} has no literal state number.";
        else if (n < 0) why = $"{s.Name} is one of the engine's always-running states (-1/-2/-3); it cannot be entered.";
        else if (s.Prop("common") == "true") why = $"{s.Name} is an engine common state, not one of this character's moves.";
        else if (s.Prop("shadowed") == "true") why = $"{s.Name} is a shadowed redefinition; the engine uses the first definition.";
        return why is null;
    }

    /// <summary>
    /// The preview plan: neutral, walk to the approach distance, then force <paramref name="stateId"/> and watch. Its single step is a
    /// <see cref="PlanStep.ForceKind"/> step with no input, which the route verifier refuses to judge.
    /// </summary>
    public static PlanResult Plan(SemanticIndex index, string stateId, int approachDistance, int tailFrames)
    {
        if (!CanPreview(index, stateId, out var why)) return new PlanResult(null, why);
        var number = int.Parse(index.Get(stateId)!.Prop("number")!, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var options = new PlanOptions { ApproachDistance = approachDistance, TailFrames = tailFrames };
        var step = new PlanStep(1, ScopeKey(stateId), PlanStep.ForceKind, "neutral", stateId, null, number, null, [], null, null, options.StepTimeout,
            ["Forced with the engine's changeState: not a command path, not proof."]);
        return new PlanResult(new InputPlan(index.CharacterId, ScopeKey(stateId), [step], options.ApproachDistance, options.NeutralFrames, options.TailFrames,
            options.MaxFrames, ["State Preview forces the state; nothing about this run is evidence that the move can be performed."]), null);
    }

    /// <summary>Reads a preview trace. Integrity is checked so a broken or foreign recording is not described, but the result is still never a verdict.</summary>
    public static PreviewReport Read(InputPlan plan, TraceLog log)
    {
        var step = plan.Steps.Count == 1 && plan.Steps[0].IsForce ? plan.Steps[0] : null;
        var notes = new List<string>();
        var driver = log.Events.OfType<DriverEvent>().ToList();
        PreviewReport Make(PreviewStatus status, string? reason, string? detail, long? forced = null, long? seen = null, MoveObservation? observed = null) =>
            new(plan.Character, step?.ToId ?? string.Empty, step?.ToState ?? 0, status, reason, detail, forced, seen, observed, notes)
            {
                PlanFingerprint = InputPlanner.Fingerprint(plan), EngineSha256 = log.Meta?.EngineSha256, EngineExecutable = log.Meta?.EngineExecutable,
                EngineVersion = log.Meta?.EngineVersion, EngineSource = log.Meta?.EngineSource
            };

        if (step is null) return Make(PreviewStatus.Inconclusive, VerifyReason.PlanMismatch, "This is not a State Preview plan.");
        if (log.Meta?.PlanFingerprint != InputPlanner.Fingerprint(plan))
            return Make(PreviewStatus.Inconclusive, VerifyReason.PlanMismatch, "The recording does not identify this preview plan.");
        var ordered = log.Events.Where(e => e is not TraceMeta).ToList();
        if (log.Issues.Count > 0 || ordered.Zip(ordered.Skip(1)).Any(p => p.Second.Frame < p.First.Frame))
            return Make(PreviewStatus.Inconclusive, VerifyReason.TraceIntegrity, "The recording has dropped, malformed or out-of-order lines: " + string.Join("; ", log.Issues.Select(i => i.Message)));
        if (driver.Count(d => d.Kind == "plan_start") != 1)
            return Make(PreviewStatus.Inconclusive, VerifyReason.TraceIntegrity, "Expected exactly one plan_start.");
        if (driver.Any(d => d.Kind is "inject_unavailable" or "driver_load_failed" or "driver_error"))
            return Make(PreviewStatus.Inconclusive, VerifyReason.InputInjectionUnavailable, "The driver could not hold P1 neutral (" +
                driver.First(d => d.Kind is "inject_unavailable" or "driver_load_failed" or "driver_error").Detail + ").");
        var frames = log.Frames.ToList();
        if (frames.Count == 0) return Make(PreviewStatus.Inconclusive, VerifyReason.NoMatchFrames, "No match was recorded.");

        var failed = driver.FirstOrDefault(d => d.Kind == "force_failed");
        if (failed is not null)
            return Make(PreviewStatus.Refused, ForceRefused, $"The engine's changeState did not enter State {step.ToState}" + (string.IsNullOrEmpty(failed.Detail) ? "." : $" ({failed.Detail})."), failed.Frame);
        var applied = driver.FirstOrDefault(d => d.Kind == "force_applied");
        if (applied is null)
        {
            var stop = driver.FirstOrDefault(d => d.Kind == "timeout");
            return Make(PreviewStatus.Inconclusive, stop is null ? ForceNotAttempted : VerifyReason.DriverTimeout,
                stop is null ? "The preview never reached the point of forcing the state." : "The driver stopped before forcing the state: " + stop.Detail);
        }

        // Samples from the force on must be contiguous, or "what followed" cannot be read.
        var after = frames.Where(f => f.Frame > applied.Frame).ToList();
        if (after.Count == 0) return Make(PreviewStatus.Inconclusive, VerifyReason.TelemetryMissing, "Nothing was recorded after the state was forced.", applied.Frame);
        if (after.Zip(after.Skip(1)).Any(p => p.Second.Frame != p.First.Frame + 1))
            return Make(PreviewStatus.Inconclusive, VerifyReason.TraceIntegrity, "Samples after the force are not contiguous.", applied.Frame);
        if (after.All(f => f.P1.State is null))
            return Make(PreviewStatus.Inconclusive, VerifyReason.TelemetryMissing, "P1's state is not readable after the force.", applied.Frame);

        var seen = after.FirstOrDefault(f => f.P1.State == step.ToState);
        var startIndex = frames.IndexOf(seen ?? after[0]);
        var complete = MoveObservation.DriverFinished(log);
        var observed = MoveObservation.Read(frames, startIndex, complete);
        if (RouteVerifier.InHitState(frames[Math.Max(0, startIndex - 1)].P2))
            notes.Add("The opponent was already in a hit state when the state was forced; its reaction below is not attributable to the preview.");
        if (seen is null)
        {
            notes.Add($"P1 was never sampled in State {step.ToState}: the state changes on its first tick (first state seen: {after[0].P1.State?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}).");
            return Make(PreviewStatus.ForcedNotSeen, null, null, applied.Frame, null, observed);
        }

        return Make(PreviewStatus.Forced, null, null, applied.Frame, seen.Frame, observed);
    }

    public static string Headline(PreviewReport r, Func<string, string?>? name) => r.Status switch
    {
        PreviewStatus.Forced => $"Preview (not proof) · {AbilityText.StateLabel(r.StateId, name)} forced" + (r.Observed is { } o ? " · " + AbilityText.Short(o) : string.Empty),
        PreviewStatus.ForcedNotSeen => $"Preview (not proof) · {AbilityText.StateLabel(r.StateId, name)} forced, but it left on its first tick" + (r.Observed is { } o2 ? " · " + AbilityText.Short(o2) : string.Empty),
        PreviewStatus.Refused => $"Preview refused by the engine: {AbilityText.StateLabel(r.StateId, name)} could not be forced",
        _ => "Preview could not run: " + (r.Reason is ForceNotAttempted ? "the state was never forced" : PlaybackInspector.Describe(r.Reason))
    };

    // ------------------------------------------------------------------ JSON

    private static readonly JsonWriterOptions Json = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ToJson(PreviewReport r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Json))
        {
            void S(string n, string? v) { if (v is null) w.WriteNull(n); else w.WriteString(n, v); }
            void L(string n, long? v) { if (v is { } x) w.WriteNumber(n, x); else w.WriteNull(n); }
            w.WriteStartObject();
            w.WriteString("schema", PreviewReport.SchemaVersion);
            w.WriteBoolean("proof", r.Proof);
            w.WriteString("notProof", PreviewReport.NotProof);
            w.WriteString("character", r.Character);
            w.WriteString("state", r.StateId);
            w.WriteString("status", r.Status.ToString());
            S("reason", r.Reason);
            S("detail", r.Detail);
            L("forceFrame", r.ForceFrame);
            L("firstSeenFrame", r.FirstSeenFrame);
            if (r.Observed is { } o) AbilityVerifier.WriteObservation(w, o); else w.WriteNull("observed");
            S("planFingerprint", r.PlanFingerprint);
            S("engineSha256", r.EngineSha256);
            S("engineExecutable", r.EngineExecutable);
            S("engineVersion", r.EngineVersion);
            S("engineSource", r.EngineSource);
            w.WritePropertyName("notes");
            w.WriteStartArray();
            foreach (var n in r.Notes) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WritePropertyName("runNotes");
            w.WriteStartArray();
            foreach (var n in r.RunNotes) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
