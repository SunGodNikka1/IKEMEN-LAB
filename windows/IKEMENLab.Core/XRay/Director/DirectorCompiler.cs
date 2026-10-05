using System.Globalization;
using System.Text;

namespace IKEMENLab.Core.XRay.Director;

/// <summary>One generated rule for the Advanced view: its name, what it does in plain words, and the compiled trigger.</summary>
public sealed record GeneratedRule(string Name, string Purpose, string Trigger);

/// <summary>
/// The generated implementation of one taught behavior: the State -1 decision block (inserted in the file the engine uses for State -1), the generated
/// states file, the DEF line that loads it, and the suppression line added to each existing rule the user chose to suppress. ASCII only (the character's
/// own bytes are never re-encoded) and deterministic: no dates, no names that are not in the model.
/// </summary>
public sealed record GeneratedCode(
    string MinusOneBlock, string StatesFile, string DefLine, IReadOnlyList<SuppressTarget> Suppress, string SuppressLine, IReadOnlyList<GeneratedRule> Rules,
    int ChaseState, int RetreatState, OwnershipPlacement Placement)
{
    public string Hash => DirectorHash.Of(MinusOneBlock + "\n--\n" + StatesFile + "\n--\n" + DefLine + "\n--\n" + Placement + "|" +
                                          string.Join(",", Suppress.Select(x => x.ControllerId)) + SuppressLine);
}

/// <summary>An existing State -1 rule that yields on knockdowns: its id and its name (the build checks the header it edits really names it).</summary>
public sealed record SuppressTarget(string ControllerId, string Name);

/// <summary>
/// Compiles a <see cref="KnockdownChaseSpec"/> into CNS — Knockdown Chase only (the compiler is deliberately not general):
/// <list type="number">
/// <item><b>Perception</b>: the plain conditions become triggers on the engine's own facts (EnemyNear posture, HitFall, P2Dist X in the fighter's units,
/// the common get-up state 5120 and its AnimTime). A knockdown is tracked once (maps) so the frequency is rolled once per knockdown.</item>
/// <item><b>Decision ownership</b>: ONE State -1 ChangeState takes the opportunity into a generated chase state; where it sits relative to the existing
/// rules is the user's placement (see <see cref="DirectorOwnership"/>).</item>
/// <item><b>Execution</b>: the chase state walks at the character's own walk speed and enters the follow-up's entry state — the state Play Ability /
/// Sequence Lab proved — with ChangeState; fallbacks go to standing, the common guard start or a generated back-walk.</item>
/// <item><b>Intention register</b>: every decision first writes xrd_beh / why / next / dist / tick / down with the SAME trigger as its ChangeState
/// (adjacent controllers), so the trace shows what the generated code decided and why. IKEMEN Lab still checks it against what executed.</item>
/// </list>
/// </summary>
public static class DirectorCompiler
{
    public const string RulePrefix = "IKEMEN Lab Director";
    public const string Generator = "ikemenlab-director/1";
    public const string BeginMarker = "; ==== IKEMEN Lab AI Director - generated - BEGIN";
    public const string EndMarker = "; ==== IKEMEN Lab AI Director - generated - END";
    public const string SuppressMarker = "; IKEMEN Lab AI Director - suppressed";
    public const string DefMarker = "; IKEMEN Lab AI Director (generated)";

    /// <summary>xrd_why: the reason code of the generated behavior's last decision.</summary>
    public const int WhyStart = 1, WhyAttack = 2, WhyRecovered = 3, WhyGiveUp = 4, WhyNoMeter = 5;

    /// <summary>Common states the generated code relies on (the engine's common1): getting up, standing, guard start.</summary>
    public const int GetUpState = 5120, StandState = 0, GuardStartState = 120, RetreatFrames = 24;

    public static GeneratedCode Compile(TaughtBehavior behavior, CharacterFacts facts, Model.SemanticIndex index)
    {
        var s = behavior.Spec;
        if (s.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        var k = facts.UnitsPerPx;
        string Px(int px) => N(px * k);
        var attack = Px(s.AttackDistance);
        var limit = Px(s.ChaseLimit);
        var chase = facts.ChaseState;
        var retreat = facts.RetreatState;
        var walkAnim = facts.HasWalkAnim ? 20 : 0;
        var rules = new List<GeneratedRule>();

        // ---------------------------------------------------------------- perception (plain condition → trigger)
        const string falling = "(EnemyNear, StateType = A && EnemyNear, MoveType = H && EnemyNear, HitFall)";
        const string lying = "EnemyNear, StateType = L";
        const string down = "(EnemyNear, StateType = L || " + falling + ")";
        const string recovered = "(EnemyNear, StateType != L && EnemyNear, MoveType != H)";
        var when = s.When switch { KnockdownWhen.Falling => falling, KnockdownWhen.Lying => lying, _ => down };
        var inRange = $"abs(P2Dist X) <= {attack}";
        var meter = s.MeterCost > 0 ? $"Power >= {s.MeterCost}" : null;
        var gettingUp = $"(EnemyNear, StateNo = {GetUpState} && EnemyNear, AnimTime >= {-s.LeadFrames})";
        var justUp = $"({recovered} && EnemyNear, Time <= {KnockdownChaseSpec.GraceFrames})";
        var timing = s.Timing == FollowUpTiming.WhileDown ? down : $"({gettingUp} || {justUp})";
        var gaveUpRecovered = s.Timing == FollowUpTiming.WhileDown ? recovered
            : $"{recovered} && (EnemyNear, Time > {KnockdownChaseSpec.GraceFrames} || abs(P2Dist X) > {attack})";
        var (fallbackState, fallbackCtrl, fallbackName) = s.Fallback switch
        {
            ChaseFallback.Block => (GuardStartState, 1, "block (guard start)"),
            ChaseFallback.Retreat => (retreat, 0, "retreat"),
            _ => (StandState, 1, "stop and reassess")
        };

        // ---------------------------------------------------------------- State -1 block
        var m1 = new StringBuilder();
        var title = $"{behavior.Family} {behavior.Id} rev {behavior.Revision.ToString(CultureInfo.InvariantCulture)}";
        m1.Append($"{BeginMarker} {title} - do not edit: regenerate it from the behavior in IKEMEN Lab (X-Ray > AI Director) ====\n");
        m1.Append($"; {Ascii(DirectorText.Sentence(behavior))}\n");
        m1.Append($"; Placement: {Ascii(DirectorText.Placement(s.Placement))}. Model {DirectorHash.Short(behavior.ModelHash)}, {Generator}.\n");
        m1.Append($"; Distances: P2Dist X is in this character's units ({N(k)} per px; localcoord {facts.LocalCoord.ToString(CultureInfo.InvariantCulture)}).\n\n");

        // A knockdown begins: roll how often (once per knockdown), then remember it.
        var onset = $"Map(xrd_kc_down) = 0 && {down}";
        Controller(m1, "-1", "knockdown seen: roll how often", "MapSet", ["AILevel > 0"], [onset], ["map(xrd_roll) = Random"]);
        Controller(m1, "-1", "knockdown seen", "MapSet", ["AILevel > 0"], [onset], ["map(xrd_kc_down) = 1"]);
        rules.Add(new GeneratedRule("knockdown seen", $"Notices a new knockdown and rolls how often ({DirectorText.Frequency(s.Frequency)}) once for it.", onset));
        // The knockdown is over (the opponent is up and not hit) and the chase is not running: the next knockdown is a new one.
        var over = $"Map(xrd_kc_down) = 1 && {recovered} && StateNo != {chase}";
        Controller(m1, "-1", "knockdown over: forget it", "MapSet", ["AILevel > 0"], [over], ["map(xrd_kc_done) = 0"]);
        Controller(m1, "-1", "knockdown over: no behavior", "MapSet", ["AILevel > 0"], [over], ["map(xrd_beh) = 0"]);
        Controller(m1, "-1", "knockdown over", "MapSet", ["AILevel > 0"], [over], ["map(xrd_kc_down) = 0"]);
        rules.Add(new GeneratedRule("knockdown over", "Forgets the knockdown once the opponent is up again and the chase has ended.", over));

        // The decision: take the knockdown.
        string[] startAll =
        [
            "AILevel > 0 && RoundState = 2",
            "Ctrl && StateType != A",
            "Map(xrd_kc_down) = 1 && Map(xrd_kc_done) = 0",
            "EnemyNear, Alive",
            when,
            $"abs(P2Dist X) <= {limit}",
            .. meter is null ? Array.Empty<string>() : [meter]
        ];
        string[] startTrigger = [$"Map(xrd_roll) < {s.FrequencyThreshold.ToString(CultureInfo.InvariantCulture)}"];
        Decision(m1, "-1", "start the chase", startAll, startTrigger, WhyStart, chase, done: false);
        Controller(m1, "-1", "Knockdown Chase: start", "ChangeState", startAll, startTrigger, [$"value = {chase}"]);
        rules.Add(new GeneratedRule("start the chase", $"{DirectorText.When(s.When)}, within {s.ChaseLimit} px, the fighter can act on the ground" +
            (meter is null ? string.Empty : $", with {s.MeterCost} power") + $" and the roll allows it → the chase (State {chase}).", string.Join(" && ", startAll.Concat(startTrigger))));
        m1.Append($"{EndMarker} {title} ====\n");

        // ---------------------------------------------------------------- generated states
        var st = new StringBuilder();
        st.Append($"; IKEMEN Lab AI Director - generated file. Do not edit: regenerate it from the behavior in IKEMEN Lab (X-Ray > AI Director).\n");
        st.Append($"; {title} - model {DirectorHash.Short(behavior.ModelHash)} - {Generator}\n");
        st.Append($"; {Ascii(DirectorText.Sentence(behavior))}\n\n");
        st.Append($"; Knockdown Chase: walk toward the downed opponent, then {Ascii(s.FollowUpName)} (State {s.FollowUpState.ToString(CultureInfo.InvariantCulture)}).\n");
        st.Append($"[Statedef {chase}]\ntype = S\nmovetype = I\nphysics = S\nanim = {walkAnim}\nctrl = 0\nvelset = 0,0\n\n");
        Controller(st, chase.ToString(CultureInfo.InvariantCulture), "face the opponent", "Turn", [], ["P2Dist X < 0"], []);
        Controller(st, chase.ToString(CultureInfo.InvariantCulture), "walk toward them", "VelSet", [], [$"abs(P2Dist X) > {attack}"], ["x = const(velocity.walk.fwd.x)"]);
        if (s.Timing == FollowUpTiming.AsTheyGetUp)
        {
            Controller(st, chase.ToString(CultureInfo.InvariantCulture), "in range: wait for them to get up", "VelSet", [], [inRange], ["x = 0"]);
            if (facts.HasStandAnim) Controller(st, chase.ToString(CultureInfo.InvariantCulture), "in range: stand", "ChangeAnim", [], [$"{inRange} && Anim != 0"], ["value = 0"]);
            if (walkAnim != 0) Controller(st, chase.ToString(CultureInfo.InvariantCulture), "out of range: walk", "ChangeAnim", [], [$"abs(P2Dist X) > {attack} && Anim != {walkAnim}"], [$"value = {walkAnim}"]);
        }

        Controller(st, chase.ToString(CultureInfo.InvariantCulture), "show the intention (Ctrl+D debug view)", "DisplayToClipboard", [], ["1"],
        [
            $"text = \"IKEMEN Lab Director - Knockdown Chase: chasing, %d px (attack at {s.AttackDistance}, chase limit {s.ChaseLimit}, give up at {s.GiveUpFrames}f)\"",
            $"params = floor(abs(P2Dist X) / {N(k)})",
            "ignorehitpause = 1"
        ]);

        // 1. close enough (and the timing holds) → the follow-up
        string[] attackTrigger = [$"{inRange} && {timing}" + (meter is null ? string.Empty : $" && {meter}")];
        Decision(st, chase.ToString(CultureInfo.InvariantCulture), "attack distance reached", [], attackTrigger, WhyAttack, s.FollowUpState, done: true);
        Controller(st, chase.ToString(CultureInfo.InvariantCulture), $"attack distance reached: {Ascii(s.FollowUpName)}", "ChangeState", [], attackTrigger,
            [$"value = {s.FollowUpState.ToString(CultureInfo.InvariantCulture)}", "ctrl = 0"]);
        rules.Add(new GeneratedRule("attack distance reached", $"Within {s.AttackDistance} px {(s.Timing == FollowUpTiming.WhileDown ? "while they are down" : "as they get up")} → {s.FollowUpName} (State {s.FollowUpState}).", attackTrigger[0]));

        // 2. the follow-up is no longer possible (meter spent) → fallback
        if (meter is not null)
        {
            string[] noMeter = [$"Power < {s.MeterCost}"];
            Decision(st, chase.ToString(CultureInfo.InvariantCulture), "follow-up no longer valid", [], noMeter, WhyNoMeter, fallbackState, done: true);
            Controller(st, chase.ToString(CultureInfo.InvariantCulture), $"follow-up no longer valid: {fallbackName}", "ChangeState", [], noMeter,
                [$"value = {fallbackState}", $"ctrl = {fallbackCtrl}"]);
            rules.Add(new GeneratedRule("follow-up no longer valid", $"The power for {s.FollowUpName} is gone → {fallbackName}.", noMeter[0]));
        }

        // 3. the opponent recovered first → fallback
        string[] recoveredTrigger = [gaveUpRecovered];
        Decision(st, chase.ToString(CultureInfo.InvariantCulture), "opponent recovered", [], recoveredTrigger, WhyRecovered, fallbackState, done: true);
        Controller(st, chase.ToString(CultureInfo.InvariantCulture), $"opponent recovered: {fallbackName}", "ChangeState", [], recoveredTrigger,
            [$"value = {fallbackState}", $"ctrl = {fallbackCtrl}"]);
        rules.Add(new GeneratedRule("opponent recovered", $"The opponent is up before the follow-up → {fallbackName}.", gaveUpRecovered));

        // 4. give up
        string[] giveUp = [$"Time >= {s.GiveUpFrames.ToString(CultureInfo.InvariantCulture)}"];
        Decision(st, chase.ToString(CultureInfo.InvariantCulture), "give up", [], giveUp, WhyGiveUp, StandState, done: true);
        Controller(st, chase.ToString(CultureInfo.InvariantCulture), "give up", "ChangeState", [], giveUp, ["value = 0", "ctrl = 1"]);
        rules.Add(new GeneratedRule("give up", $"{s.GiveUpFrames} frames of chasing → stop and reassess.", giveUp[0]));

        if (s.Fallback == ChaseFallback.Retreat)
        {
            var backAnim = facts.HasWalkBackAnim ? 21 : walkAnim;
            st.Append($"; Retreat (the fallback): walk back for {RetreatFrames} frames, then stand with control.\n");
            st.Append($"[Statedef {retreat}]\ntype = S\nmovetype = I\nphysics = S\nanim = {backAnim}\nctrl = 0\nvelset = 0,0\n\n");
            Controller(st, retreat.ToString(CultureInfo.InvariantCulture), "walk back", "VelSet", [], ["1"], ["x = const(velocity.walk.back.x)"]);
            Controller(st, retreat.ToString(CultureInfo.InvariantCulture), "retreat done", "ChangeState", [], [$"Time >= {RetreatFrames}"], ["value = 0", "ctrl = 1"]);
        }

        var def = $"{facts.StKey} = {CharacterFacts.GeneratedFile}   {DefMarker} - remove this line and the file to retire it";
        var suppressLine = $"triggerall = Map(xrd_kc_down) = 0   {SuppressMarker} on knockdowns by {behavior.Family} {behavior.Id} (generated)";
        var suppress = s.Placement == OwnershipPlacement.SuppressSpecific
            ? s.Suppress.OrderBy(x => x, StringComparer.Ordinal).Select(id => new SuppressTarget(id, index.Get(id)?.Name ?? string.Empty)).ToList()
            : [];
        return new GeneratedCode(m1.ToString(), st.ToString(), def, suppress, suppressLine, rules, chase, retreat, s.Placement);
    }

    /// <summary>The intention-register writes for one decision: the same triggers as its ChangeState, placed right before it.</summary>
    private static void Decision(StringBuilder sb, string state, string what, IReadOnlyList<string> all, IReadOnlyList<string> trigger, int why, int next, bool done)
    {
        var at = $"register: {what}";
        Controller(sb, state, at + " (behavior)", "MapSet", all, trigger, [$"map(xrd_beh) = {TaughtBehavior.Slot}"]);
        Controller(sb, state, at + " (reason)", "MapSet", all, trigger, [$"map(xrd_why) = {why}"]);
        Controller(sb, state, at + " (next)", "MapSet", all, trigger, [$"map(xrd_next) = {next}"]);
        Controller(sb, state, at + " (distance)", "MapSet", all, trigger, ["map(xrd_dist) = abs(P2Dist X)"]);
        Controller(sb, state, at + " (tick)", "MapSet", all, trigger, ["map(xrd_tick) = GameTime"]);
        // Redirects are parenthesised: inside ifelse's argument list a bare "EnemyNear," would read as an argument separator.
        Controller(sb, state, at + " (enemy posture)", "MapSet", all, trigger, ["map(xrd_down) = ifelse((EnemyNear, StateType = L), 2, ifelse((EnemyNear, StateType = A), 1, 0))"]);
        if (done) Controller(sb, state, at + " (this knockdown is handled)", "MapSet", all, trigger, ["map(xrd_kc_done) = 1"]);
    }

    private static void Controller(StringBuilder sb, string state, string name, string type, IReadOnlyList<string> all, IReadOnlyList<string> trigger, IReadOnlyList<string> body)
    {
        sb.Append($"[State {state}, {RulePrefix} - {name}]\ntype = {type}\n");
        foreach (var a in all) sb.Append($"triggerall = {a}\n");
        foreach (var t in trigger) sb.Append($"trigger1 = {t}\n");
        foreach (var b in body) sb.Append(b).Append('\n');
        sb.Append('\n');
    }

    internal static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Generated text is ASCII only, so the character's files are never re-encoded.</summary>
    public static string Ascii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(ch switch
            {
                '—' or '–' => '-', '→' => '>', '·' => '-', '“' or '”' => '"', '‘' or '’' => '\'', '…' => '.',
                < ' ' or > '~' => '?',
                _ => ch
            });
        return sb.ToString();
    }
}
