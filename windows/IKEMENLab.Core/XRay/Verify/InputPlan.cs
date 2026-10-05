using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Verify;

/// <summary>One tick of scripted input: the complete set of logical keys held (U D F B a b c x y z s). F/B are relative to facing; the injector adapter maps them.</summary>
public sealed record InputFrame(IReadOnlyList<string> Keys);

/// <summary>
/// A Sequence Lab step that is not a move: the driver holds or releases keys instead of feeding a command and expecting a state.
/// <list type="bullet">
/// <item><c>hold</c>: hold <see cref="Keys"/> (F = walk forward, B = walk back) for <see cref="Frames"/> ticks.</item>
/// <item><c>release</c>: hold nothing for <see cref="Frames"/> ticks (Wait).</item>
/// <item><c>chase</c>: hold F until the players are within <see cref="Distance"/>, or <see cref="Frames"/> ticks pass; with
/// <see cref="StopIfOpponentRecovers"/> the run stops as soon as the opponent has control again.</item>
/// <item><c>jump</c>: press U and expect P1 to be airborne within <see cref="Frames"/> ticks.</item>
/// </list>
/// With <see cref="WaitForControl"/> the step starts only once P1 can act. Route and ability plans never contain one.
/// <para>
/// <c>handover</c> (Teach AI fixtures only, never a Sequence Lab step): give P1's input slot back and put P1 on the engine's AI at <see cref="AiLevel"/>;
/// with <see cref="OpponentAiLevel"/> &gt; 0 also P2. From then on the character's own AI decides; nothing is injected for a handed-over player.
/// </para>
/// </summary>
public sealed record StepAction(string Kind, int? Frames = null, int? Distance = null, bool StopIfOpponentRecovers = false, IReadOnlyList<string>? Keys = null, bool WaitForControl = true)
{
    public const string Hold = "hold", Release = "release", Chase = "chase", Jump = "jump", Handover = "handover";
    /// <summary>Handover only: the AI level P1 is given (1–8).</summary>
    public int? AiLevel { get; init; }
    /// <summary>Handover only: the AI level P2 is given; null or 0 leaves P2 idle.</summary>
    public int? OpponentAiLevel { get; init; }
}

/// <summary>
/// One route step as the driver will run it. The driver waits until <see cref="WaitState"/> (and contact / tick) hold, feeds
/// <see cref="Input"/>, then expects P1 to be in <see cref="ExpectState"/> within <see cref="TimeoutFrames"/>.
/// </summary>
public sealed record PlanStep(
    int Index, string EdgeId, string Kind, string FromId, string ToId, int? FromState, int ToState,
    string? Command, IReadOnlyList<InputFrame> Input, string? Contact, int? EarliestTick, int TimeoutFrames, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// The kind of a State Preview step: no input at all, the driver forces <see cref="ToState"/> with the engine's own <c>changeState</c>. Only
    /// <see cref="Playback.StatePreview"/> builds such a step; the route planner never does, and the route verifier refuses to judge one.
    /// </summary>
    public const string ForceKind = "Force";
    public bool IsForce => Kind == ForceKind;

    /// <summary>Sequence Lab only: a movement / wait / chase / jump step (no command, no expected state). Null for every move step.</summary>
    public StepAction? Action { get; init; }
    public bool IsAction => Action is not null;

    /// <summary>Sequence Lab only: this move is one of the sequence's attacks (its contact is judged). False (and not serialised) everywhere else.</summary>
    public bool Attack { get; init; }
}

public sealed record InputPlan(
    string Character, string RouteKey, IReadOnlyList<PlanStep> Steps, int ApproachDistance, int NeutralFrames, int TailFrames,
    int MaxFrames, IReadOnlyList<string> Warnings)
{
    public const string SchemaVersion = "ikemenlab.xray.plan/1";
}

public sealed record PlanResult(InputPlan? Plan, string? RefusedReason);

public sealed record PlanOptions
{
    /// <summary>P1 walks forward until the players are this close (engine distance units) before the route starts.</summary>
    public int ApproachDistance { get; init; } = 60;
    public int NeutralFrames { get; init; } = 20;
    /// <summary>Frames watched after the last step so a drop right after the final hit is still seen.</summary>
    public int TailFrames { get; init; } = 20;
    public int HoldFrames { get; init; } = 2;
    public int StepTimeout { get; init; } = 45;
    public int MaxFrames { get; init; } = 1800;
}

/// <summary>
/// Turns a candidate route into a deterministic, reactive input plan. It never guesses: a route it cannot script (starts in a
/// state, needs a link through neutral, has a dynamic or missing command) is refused with the reason.
/// </summary>
public static class InputPlanner
{
    private static readonly string[] Buttons = ["a", "b", "c", "x", "y", "z", "s"];

    public static PlanResult Plan(CandidateGraph graph, ComboRoute route, PlanOptions? options = null)
    {
        options ??= new PlanOptions();
        if (route.StartState != CandidateGraph.NeutralId)
            return new PlanResult(null, $"The route starts in {route.StartState}; only neutral-start routes can be scripted (the match must begin with nobody in a move).");
        if (route.Steps.Count == 0) return new PlanResult(null, "The route has no steps.");

        var warnings = new List<string>();
        var steps = new List<PlanStep>();
        foreach (var (step, i) in route.Steps.Select((s, i) => (s, i + 1)))
        {
            var e = step.Edge;
            if (e.Kind is EdgeKind.Link or EdgeKind.Recovery)
                return new PlanResult(null, $"Step {i} ({e.Id}) goes through neutral ({e.Kind}); link timing is not modelled, so it cannot be scripted.");
            var to = graph.Move(e.To);
            if (to is null) return new PlanResult(null, $"Step {i}: target {e.To} is not a move state.");
            var from = e.From == CandidateGraph.NeutralId ? null : graph.Move(e.From);
            if (i > 1 && from is null) return new PlanResult(null, $"Step {i}: source {e.From} is not a move state.");

            var notes = new List<string>();
            IReadOnlyList<InputFrame> input = [];
            string? command = null;
            if (e.Kind is EdgeKind.Start or EdgeKind.Cancel || (e.Kind == EdgeKind.Chain && e.Commands.Count > 0))
            {
                if (e.Commands.Count == 0)
                    return new PlanResult(null, $"Step {i} ({e.Id}) needs an input but the gate names no command.");
                var built = BuildInput(graph.Index, e.Commands, options, notes);
                if (built is null)
                    return new PlanResult(null, $"Step {i} ({e.Id}): the command {string.Join("+", e.Commands)} could not be read literally from the CMD.");
                input = built;
                command = string.Join("+", e.Commands);
            }

            var contact = e.Contact == ContactRequirement.None ? null : e.Contact.ToString().ToLowerInvariant();
            steps.Add(new PlanStep(i, e.Id, e.Kind.ToString(), e.From, e.To, from?.Number, to.Number, command, input, contact,
                e.EarliestTick, options.StepTimeout, notes));
            if (e.Unmodelled.Count > 0) warnings.Add($"Step {i} ({e.Id}) has {e.Unmodelled.Count} unmodelled condition(s); the script may not satisfy them.");
        }

        return new PlanResult(new InputPlan(graph.Index.CharacterId, route.Key, steps, options.ApproachDistance, options.NeutralFrames,
            options.TailFrames, options.MaxFrames, warnings), null);
    }

    /// <summary>Expands the command(s) of a gate into per-tick held key sets. Several commands are overlaid so they finish together.</summary>
    internal static IReadOnlyList<InputFrame>? BuildInput(SemanticIndex index, IReadOnlyList<string> commands, PlanOptions options, List<string> notes)
    {
        var sequences = new List<List<string[]>>();
        foreach (var name in commands)
        {
            var obj = index.Get("cmd:" + name);
            if (obj is null || !obj.Props.TryGetValue("raw", out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
            var time = obj.Props.TryGetValue("time", out var t) && int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tv) ? tv : 15;
            var hold = options.HoldFrames;
            var seq = Expand(raw, hold);
            if (seq.Count > time && hold > 1) { hold = 1; seq = Expand(raw, hold); }
            if (seq.Count > time) notes.Add($"Command {name} needs {seq.Count} ticks but its time is {time}; it may not register.");
            sequences.Add(seq);
        }

        var length = sequences.Max(s => s.Count);
        var frames = new List<InputFrame>();
        for (var f = 0; f < length; f++)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var s in sequences)
            {
                var k = f - (length - s.Count); // right-align so every command's last step lands on the same tick
                if (k >= 0) foreach (var key in s[k]) keys.Add(key);
            }

            frames.Add(new InputFrame(keys.ToList()));
        }

        frames.Add(new InputFrame([]));
        return frames;
    }

    private static List<string[]> Expand(string raw, int hold)
    {
        var frames = new List<string[]>();
        string[]? previous = null;
        var previousWasRelease = false;
        foreach (var step in CommandIndexer.Tokenize(raw))
        {
            var keys = step.Keys.SelectMany(Logical).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
            if (keys.Length == 0) continue;
            // The same key twice in a row needs a released tick between the two presses, or the engine sees one long press.
            if (previous is not null && !previousWasRelease && previous.Intersect(keys).Any()) frames.Add([]);
            var n = step.Hold ? Math.Max(hold, 6) : step.ReleaseFrames is { } r ? Math.Max(hold, r) : hold;
            for (var i = 0; i < n; i++) frames.Add(keys);
            if (step.Release) frames.Add([]);
            previous = keys;
            previousWasRelease = step.Release;
        }

        return frames;
    }

    /// <summary>MUGEN key names → logical keys (DF = D+F). Unknown names pass through so a bad CMD is visible in the plan, not hidden.</summary>
    private static IEnumerable<string> Logical(string key)
    {
        var k = key.Trim();
        switch (k)
        {
            case "DF": return ["D", "F"];
            case "DB": return ["D", "B"];
            case "UF": return ["U", "F"];
            case "UB": return ["U", "B"];
            case "F" or "B" or "U" or "D": return [k];
            case "start": return ["s"];
        }

        var lower = k.ToLowerInvariant();
        return Buttons.Contains(lower) ? [lower] : [k];
    }

    // ------------------------------------------------------------------ serialisation

    private static readonly JsonWriterOptions Json = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ToJson(InputPlan plan)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Json))
        {
            w.WriteStartObject();
            w.WriteString("schema", InputPlan.SchemaVersion);
            w.WriteString("character", plan.Character);
            w.WriteString("route", plan.RouteKey);
            w.WriteNumber("approachDistance", plan.ApproachDistance);
            w.WriteNumber("neutralFrames", plan.NeutralFrames);
            w.WriteNumber("tailFrames", plan.TailFrames);
            w.WriteNumber("maxFrames", plan.MaxFrames);
            w.WritePropertyName("steps");
            w.WriteStartArray();
            foreach (var s in plan.Steps)
            {
                w.WriteStartObject();
                w.WriteNumber("index", s.Index);
                w.WriteString("edge", s.EdgeId);
                w.WriteString("kind", s.Kind);
                w.WriteString("from", s.FromId);
                w.WriteString("to", s.ToId);
                if (s.FromState is { } fs) w.WriteNumber("fromState", fs); else w.WriteNull("fromState");
                w.WriteNumber("toState", s.ToState);
                if (s.Command is null) w.WriteNull("command"); else w.WriteString("command", s.Command);
                if (s.Contact is null) w.WriteNull("contact"); else w.WriteString("contact", s.Contact);
                if (s.EarliestTick is { } et) w.WriteNumber("earliestTick", et); else w.WriteNull("earliestTick");
                w.WriteNumber("timeoutFrames", s.TimeoutFrames);
                w.WritePropertyName("input");
                w.WriteStartArray();
                foreach (var f in s.Input)
                {
                    w.WriteStartArray();
                    foreach (var k in f.Keys) w.WriteStringValue(k);
                    w.WriteEndArray();
                }

                w.WriteEndArray();
                w.WritePropertyName("notes");
                w.WriteStartArray();
                foreach (var n in s.Notes) w.WriteStringValue(n);
                w.WriteEndArray();
                // Only Sequence Lab steps carry these, so every route / ability / preview plan's JSON (and fingerprint) is unchanged.
                if (s.Attack) w.WriteBoolean("attack", true);
                if (s.Action is { } a)
                {
                    w.WritePropertyName("action");
                    w.WriteStartObject();
                    w.WriteString("kind", a.Kind);
                    if (a.Frames is { } fr) w.WriteNumber("frames", fr); else w.WriteNull("frames");
                    if (a.Distance is { } d) w.WriteNumber("distance", d); else w.WriteNull("distance");
                    w.WriteBoolean("stopIfOpponentRecovers", a.StopIfOpponentRecovers);
                    w.WriteBoolean("waitForControl", a.WaitForControl);
                    w.WritePropertyName("keys");
                    w.WriteStartArray();
                    foreach (var k in a.Keys ?? []) w.WriteStringValue(k);
                    w.WriteEndArray();
                    // Only a handover carries these, so every other plan's JSON (and fingerprint) is unchanged.
                    if (a.AiLevel is { } ai) w.WriteNumber("aiLevel", ai);
                    if (a.OpponentAiLevel is { } oai) w.WriteNumber("opponentAiLevel", oai);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WritePropertyName("warnings");
            w.WriteStartArray();
            foreach (var x in plan.Warnings) w.WriteStringValue(x);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Identity of the exact inputs, timing and route being tested; not a signature of authenticity.</summary>
    public static string Fingerprint(InputPlan plan) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(ToJson(plan)))));

    /// <summary>
    /// Newlines are normalised to CRLF before hashing. Utf8JsonWriter uses the platform newline, and every fingerprint recorded so far came from Windows (CRLF);
    /// normalising keeps those valid and makes the same plan hash identically on any platform.
    /// </summary>
    private static string Canonical(string json) => json.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>Reads a plan written by <see cref="ToJson"/>. Throws <see cref="FormatException"/> when it is not a plan.</summary>
    public static InputPlan FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.GetProperty("schema").GetString() != InputPlan.SchemaVersion) throw new FormatException("Not an ikemenlab.xray.plan/1 document.");
            int? OptInt(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            string? OptStr(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var steps = new List<PlanStep>();
            foreach (var s in r.GetProperty("steps").EnumerateArray())
            {
                var input = s.GetProperty("input").EnumerateArray().Select(f => new InputFrame(f.EnumerateArray().Select(k => k.GetString()!).ToList())).ToList();
                StepAction? action = null;
                if (s.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.Object)
                    action = new StepAction(a.GetProperty("kind").GetString()!, OptInt(a, "frames"), OptInt(a, "distance"), a.GetProperty("stopIfOpponentRecovers").GetBoolean(),
                        a.GetProperty("keys").EnumerateArray().Select(k => k.GetString()!).ToList(), a.GetProperty("waitForControl").GetBoolean())
                        { AiLevel = OptInt(a, "aiLevel"), OpponentAiLevel = OptInt(a, "opponentAiLevel") };
                steps.Add(new PlanStep(s.GetProperty("index").GetInt32(), s.GetProperty("edge").GetString()!, s.GetProperty("kind").GetString()!,
                    s.GetProperty("from").GetString()!, s.GetProperty("to").GetString()!, OptInt(s, "fromState"), s.GetProperty("toState").GetInt32(),
                    OptStr(s, "command"), input, OptStr(s, "contact"), OptInt(s, "earliestTick"), s.GetProperty("timeoutFrames").GetInt32(),
                    s.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToList())
                    { Action = action, Attack = s.TryGetProperty("attack", out var atk) && atk.ValueKind == JsonValueKind.True });
            }

            return new InputPlan(r.GetProperty("character").GetString()!, r.GetProperty("route").GetString()!, steps, r.GetProperty("approachDistance").GetInt32(),
                r.GetProperty("neutralFrames").GetInt32(), r.GetProperty("tailFrames").GetInt32(), r.GetProperty("maxFrames").GetInt32(),
                r.GetProperty("warnings").EnumerateArray().Select(n => n.GetString()!).ToList());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("The plan JSON could not be read: " + ex.Message, ex);
        }
    }

    /// <summary>The plan as a Lua chunk (<c>return {…}</c>) the driver loads with dofile. Only literals, so nothing in a plan can execute.</summary>
    public static string ToLua(InputPlan plan)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") + "\"";
        static string N(int? v) => v is { } n ? n.ToString(CultureInfo.InvariantCulture) : "nil";
        var sb = new StringBuilder();
        sb.Append("return {\n");
        sb.Append($"  character = {Q(plan.Character)}, route = {Q(plan.RouteKey)}, fingerprint = {Q(Fingerprint(plan))},\n");
        sb.Append($"  approachDistance = {plan.ApproachDistance}, neutralFrames = {plan.NeutralFrames}, tailFrames = {plan.TailFrames}, maxFrames = {plan.MaxFrames},\n");
        sb.Append("  steps = {\n");
        foreach (var s in plan.Steps)
        {
            sb.Append($"    {{ index = {s.Index}, edge = {Q(s.EdgeId)}, kind = {Q(s.Kind)}, fromState = {N(s.FromState)}, toState = {s.ToState}, ");
            sb.Append($"contact = {(s.Contact is null ? "nil" : Q(s.Contact))}, earliestTick = {N(s.EarliestTick)}, timeout = {s.TimeoutFrames},");
            // Only a preview step carries the flag, so every route plan's Lua is exactly what it was before previews existed.
            sb.Append(s.IsForce ? " force = true," : string.Empty);
            if (s.Action is { } act)
                sb.Append($" action = {Q(act.Kind)}, frames = {N(act.Frames)}, distance = {N(act.Distance)}, stopOnRecover = {(act.StopIfOpponentRecovers ? "true" : "false")}, " +
                          $"waitCtrl = {(act.WaitForControl ? "true" : "false")}, keys = {{{string.Join(",", (act.Keys ?? []).Select(Q))}}}," +
                          (act.AiLevel is { } ai ? $" aiLevel = {ai}," : string.Empty) + (act.OpponentAiLevel is { } oai ? $" opponentAi = {oai}," : string.Empty));
            sb.Append('\n');
            sb.Append("      input = {");
            sb.Append(string.Join(", ", s.Input.Select(f => "{" + string.Join(",", f.Keys.Select(Q)) + "}")));
            sb.Append("} },\n");
        }

        sb.Append("  },\n}\n");
        return sb.ToString();
    }
}
