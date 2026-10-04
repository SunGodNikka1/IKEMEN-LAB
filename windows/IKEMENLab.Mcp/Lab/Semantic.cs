using System.Globalization;
using System.Text.Json.Nodes;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Sequences;

namespace IKEMENLab.Mcp.Lab;

/// <summary>
/// Small, readable JSON slices of X-Ray's own results. Names come first (the user's Phase-1 names through <see cref="SemanticIndex.NameOf"/>), raw ids and
/// state numbers are secondary fields. These helpers only re-shape what Core decided; they never judge anything themselves.
/// </summary>
internal static class Semantic
{
    public const string StaticBasis =
        "Static reading of the character files (what the code says, not what happens in a match). Nothing here was observed at runtime; play_ability, " +
        "preview_state and test_sequence produce runtime results.";

    /// <summary>{ name, id } plus the state number for states and X-Ray's own name when the user renamed it.</summary>
    public static JsonObject Ref(SemanticIndex index, string id)
    {
        var o = new JsonObject { ["name"] = index.Get(id) is null ? id : index.NameOf(id), ["id"] = id };
        if (id.StartsWith("state:", StringComparison.Ordinal) && int.TryParse(id["state:".Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            o["state"] = n;
        if (index.Get(id) is not null && index.Names.IsRenamed(id)) o["xrayName"] = index.Names.Default(id);
        return o;
    }

    public static string ConfidenceText(Confidence c) => c switch
    {
        Confidence.StaticProven => "static: literal in the files",
        Confidence.Inferred => "inferred (heuristic)",
        Confidence.RuntimeVerified => "runtime-verified",
        _ => "unknown"
    };

    public static string VerdictText(SequenceVerdict v) => v switch
    {
        SequenceVerdict.TrueCombo => "True combo",
        SequenceVerdict.ConnectedSequence => "Connected sequence",
        SequenceVerdict.DidNotConnect => "Did not connect",
        _ => "Could not test"
    };

    public static string VerdictText(string verdict) => Enum.TryParse<SequenceVerdict>(verdict, out var v) ? VerdictText(v) : verdict;

    public static string AbilityStatusText(AbilityStatus s) => s switch
    {
        AbilityStatus.Performed => "Performed",
        AbilityStatus.NotPerformed => "Not performed",
        _ => "Could not be tested"
    };

    /// <summary>"Normal A (200)"; X-Ray's own "State 0 (engine common)" already carries its number.</summary>
    public static string StateLabel(SemanticIndex index, int state)
    {
        var n = state.ToString(CultureInfo.InvariantCulture);
        if (index.Get("state:" + n) is null) return "State " + n;
        var name = index.NameOf("state:" + n);
        return name.StartsWith("State " + n, StringComparison.Ordinal) ? name : $"{name} ({n})";
    }

    public static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());

    public static JsonNode? Num(double? v) => v is { } x ? JsonValue.Create(Math.Round(x, 2)) : null;
    public static JsonNode? Num(long? v) => v is { } x ? JsonValue.Create(x) : null;
    public static JsonNode? Num(int? v) => v is { } x ? JsonValue.Create(x) : null;
    public static JsonNode? Bool(bool? v) => v is { } x ? JsonValue.Create(x) : null;

    /// <summary>What followed a move (Play Ability or State Preview): measurements of this one run.</summary>
    public static JsonObject Observation(SemanticIndex index, MoveObservation o)
    {
        var obj = new JsonObject
        {
            ["connected"] = o.Connected,
            ["contactFrame"] = Num(o.ContactFrame),
            ["damage"] = Num(o.Damage),
            ["opponent"] = new JsonObject
            {
                ["hit"] = Bool(o.OpponentHit), ["blocked"] = Bool(o.OpponentGuarded), ["launched"] = Bool(o.OpponentAirborne), ["knockedDown"] = Bool(o.OpponentKnockedDown)
            },
            ["control"] = o.KeptControl == true ? "kept control throughout"
                : o.FramesUntilControl is { } c ? $"back in control {c} frames after the move started (frame {o.ControlFrame})"
                : "not back in control while watched",
            ["states"] = Strings(o.P1States.Take(8).Select(s => StateLabel(index, s)).Concat(o.P1States.Count > 8 || o.P1StatesTruncated ? new[] { "…" } : Array.Empty<string>())),
            ["watchComplete"] = o.ObservationComplete
        };
        if (o.Situation is { } s) obj["situation"] = Situation(s);
        if (o.Missing.Count > 0) obj["notReadableOnThisBuild"] = Strings(o.Missing);
        return obj;
    }

    public static JsonObject Situation(SituationSnapshot s) => new()
    {
        ["text"] = s.Describe(),
        ["frame"] = s.Frame,
        ["distance"] = Num(s.Distance),
        ["opponent"] = s.OpponentPosture,
        ["opponentState"] = Num(s.OpponentState),
        ["opponentCanAct"] = Bool(s.OpponentCanAct),
        ["youCanAct"] = Bool(s.YouCanAct),
        ["yourMeter"] = Num(s.YourPower),
        ["framesBeforeOpponent"] = Num(s.FramesBeforeOpponent)
    };

    /// <summary>One step of a sequence run, as the Sequence Lab card shows it.</summary>
    public static JsonObject Step(SequenceStepResult s)
    {
        var o = new JsonObject
        {
            ["step"] = s.Index,
            ["label"] = s.Label,
            ["kind"] = s.Kind,
            ["outcome"] = s.Outcome.ToString(),
            ["result"] = s.Why ?? s.Outcome switch
            {
                SequenceStepOutcome.Done when s.Connected == true => $"Done — connected at frame {s.ContactFrame}" + (s.Damage is { } d ? $" ({d:0.##} damage)" : string.Empty),
                SequenceStepOutcome.Done when s.Connected == false => "Done — but it did not touch the opponent",
                SequenceStepOutcome.Done => $"Done (frame {s.StartFrame}" + (s.EndFrame is { } e ? $"–{e}" : string.Empty) + ")",
                SequenceStepOutcome.NotReached => "Not reached",
                _ => s.Reason is { } r ? ExperimentText.Plain(r) : "Failed"
            }
        };
        if (s.Reason is { } reason) o["reason"] = reason;
        if (s.Connected is { } c) o["connected"] = c;
        if (s.StartFrame is { } sf) o["frames"] = s.EndFrame is { } ef ? $"{sf}–{ef}" : sf.ToString(CultureInfo.InvariantCulture);
        if (s.DistanceAtStart is { } dist) o["distanceAtStart"] = Math.Round(dist, 1);
        return o;
    }

    /// <summary>A sequence run's verdict and per-step results (the same report the Sequence Lab shows).</summary>
    public static JsonObject SequenceRun(SequenceReport r, string? runId)
    {
        var o = new JsonObject
        {
            ["verdict"] = VerdictText(r.Verdict),
            ["verdictCode"] = r.Verdict.ToString(),
            ["summary"] = r.Summary,
            ["steps"] = new JsonArray(r.Steps.Select(s => (JsonNode)Step(s)).ToArray()),
            ["attacks"] = $"{r.ConnectedAttacks} of {r.Attacks} connected",
            ["damage"] = Num(r.Damage)
        };
        if (r.FailedStep is { } f) o["failedStep"] = f;
        if (r.OutOfHitstunFrames is { } gap) o["opponentOutOfHitstunFrames"] = gap;
        if (r.OpponentCouldActFrames is { } act) o["opponentCouldActFrames"] = act;
        if (r.End is { } e)
            o["end"] = new JsonObject
            {
                ["frame"] = e.Frame, ["distance"] = Num(e.Distance), ["opponent"] = e.OpponentPosture, ["opponentState"] = Num(e.OpponentState),
                ["youCanAct"] = Bool(e.YouCanAct), ["yourMeter"] = Num(e.YourPower)
            };
        if (r.Notes.Count > 0) o["notes"] = Strings(r.Notes.Where(n => !n.StartsWith("Trace sha256", StringComparison.Ordinal) && !n.StartsWith("Sandbox", StringComparison.Ordinal)).Take(6));
        if (runId is not null) o["runId"] = runId;
        o["evidence"] = "Each move step's start is the route verifier's step check in this run (runtime.transition-observed). True combo needs the opponent in hitstun " +
                        "without control on every sample between the first and last hit; contact, damage and gaps are measurements of this run, not claims about the character.";
        return o;
    }

    /// <summary>One experiment in a list.</summary>
    public static JsonObject ExperimentRow(ExperimentSummary e, SequenceReport? last = null) => new()
    {
        ["experimentId"] = e.Id,
        ["created"] = e.CreatedUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
        ["sequence"] = new JsonObject { ["name"] = e.Scope.SequenceName, ["id"] = e.Scope.SequenceId, ["version"] = e.Scope.SequenceVersion },
        ["steps"] = e.Steps,
        ["trials"] = e.Requested == e.Completed ? e.Completed.ToString(CultureInfo.InvariantCulture) : $"{e.Completed} of {e.Requested}",
        ["headline"] = ExperimentText.Headline(e, last),
        ["successes"] = e.Successes,
        ["trueCombos"] = e.TrueCombos,
        ["couldNotTest"] = e.Untestable,
        ["origin"] = e.Origin
    };

    public static JsonObject Scope(ExperimentScope s) => new()
    {
        ["text"] = ExperimentText.ScopeText(s),
        ["characterFiles"] = s.CharacterHash,
        ["engineSha256"] = s.EngineSha256,
        ["dummy"] = s.Dummy,
        ["stage"] = s.Stage,
        ["approachDistance"] = s.ApproachDistance,
        ["sequence"] = new JsonObject { ["name"] = s.SequenceName, ["id"] = s.SequenceId, ["version"] = s.SequenceVersion },
        ["planFingerprint"] = s.PlanFingerprint
    };

    /// <summary>The experiment's statistics, in the Sequence Lab's words.</summary>
    public static JsonObject Stats(ExperimentSummary s)
    {
        string? Pct(double? r) => r is { } v ? (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : null;
        var o = new JsonObject
        {
            ["completed"] = s.Completed,
            ["requested"] = s.Requested,
            ["stopped"] = s.Stopped,
            ["succeeded"] = s.Successes,
            ["successRate"] = Pct(s.SuccessRate),
            ["trueCombos"] = s.TrueCombos,
            ["trueComboRate"] = Pct(s.TrueComboRate),
            ["couldNotTest"] = s.Untestable,
            ["attacksConnected"] = $"{s.AttacksConnected} of {s.AttacksTried}",
            ["connectionRate"] = Pct(s.ConnectionRate),
            ["failureReasons"] = new JsonArray(s.FailureReasons.Select(f => (JsonNode)new JsonObject { ["reason"] = ExperimentText.Plain(f.Reason), ["count"] = f.Count }).ToArray())
        };
        if (s.DamageRange is { } d) o["damage"] = new JsonObject { ["min"] = Num(d.Min), ["average"] = Num(d.Average), ["max"] = Num(d.Max) };
        if (s.EndDistanceRange is { } e) o["endDistance"] = new JsonObject { ["min"] = Num(e.Min), ["average"] = Num(e.Average), ["max"] = Num(e.Max) };
        if (s.EndOpponent.Count > 0) o["opponentAtEnd"] = new JsonArray(s.EndOpponent.Select(x => (JsonNode)new JsonObject { ["posture"] = x.Posture, ["count"] = x.Count }).ToArray());
        if (s.AverageSeconds is { } sec) o["secondsPerTrial"] = Math.Round(sec, 1);
        return o;
    }
}
