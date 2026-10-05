using IKEMENLab.Core.XRay.Runtime;
using static IKEMENLab.Core.XRay.Behavior.BehaviorConditions;
using T = IKEMENLab.Core.XRay.Behavior.BehaviorTemplates;

namespace IKEMENLab.Core.XRay.Behavior;

public enum StepKind { Situation, Approach, Distance, Attack, Outcome, Guard, Jump, Retreat, Meter }

/// <summary>One step of an episode, kept as data (state numbers, distances) so it is rendered with the names in effect when it is shown.</summary>
/// <param name="Code">enemy-falling, enemy-lying, enemy-getting-up, enemy-jump, enemy-attack, enemy-projectile, approach, retreat, distance, attack, guard,
/// jump, meter, connected, blocked, whiffed, unknown, hit, not-hit.</param>
public sealed record EpisodeStep(long Frame, StepKind Kind, string Code, int? State = null, double? From = null, double? To = null, string? Detail = null);

/// <summary>
/// One complete behavioral episode found in a recorded run: which template, the exact frame range, the steps, the conditions that held, the fighter's
/// states that took part and the outcome. Nothing in it says which AI rule produced it.
/// </summary>
public sealed record BehaviorEpisode(
    string Template, int Round, long StartFrame, long EndFrame, IReadOnlyList<EpisodeStep> Steps, IReadOnlyList<Condition> Conditions,
    IReadOnlyList<int> ActionStates, string Outcome, IReadOnlyList<string> Notes);

/// <summary>What the detector found in one trace: episodes, how many situations started a template but did not complete it, and what it could not read.</summary>
public sealed record Detection(
    IReadOnlyList<BehaviorEpisode> Episodes, IReadOnlyDictionary<string, int> Incomplete, IReadOnlyList<string> Missing, IReadOnlyList<string> Notes,
    int Rounds, int Frames, string Control, double? SubjectAiLevel, bool AiControlled)
{
    /// <summary>The fighter's DEF localcoord width the run was read with (null = 320); the timeline and Why re-read the trace on the same scale.</summary>
    public int? SubjectLocalWidth { get; init; }
}

/// <summary>
/// Finds episodes of the fixed templates in a recorded run where P1 (the character being studied) fights on the engine's AI. Pure and deterministic:
/// the same trace always gives the same episodes. Runs are split at round changes so nothing spans a round reset.
/// </summary>
public static class BehaviorDetector
{
    /// <summary>Bumped whenever a template rule changes; stored runs with another version are re-detected from their trace.</summary>
    // 2: approaches and retreats need the fighter's own velocity (pushes no longer count).
    // 3: a wake-up must be a real get-up (not a juggle off the ground, control back within 90 frames); a super attempt must spend power.
    // 4: Super states are the entry states of Super abilities only; the chase notes a follow-up only when it spent power.
    // 5: one Ground Follow-Up per time on the ground; a Super-labelled move must spend at least half a bar.
    // 6: a guard (or a hit) answers only the opponent's attack it falls in, never one that ended before their next attack started.
    // 7: both fighters on one coordinate scale (BehaviorCoordinates): an opponent with another localcoord is no longer measured in its own units; a
    //    fighter's movement is its own velocity capped by the change in distance (both camera-free), not its screen-relative position change.
    public const int Version = 7;

    /// <param name="superStates">State numbers of the character's Super abilities (static, inferred category).</param>
    /// <param name="launchedAiLevel">The AI level P1 was launched with, when the trace cannot report it.</param>
    /// <param name="subjectLocalWidth">The fighter's DEF localcoord width (null = 320): distances are read in 320-wide units (<see cref="BehaviorCoordinates"/>).</param>
    public static Detection Detect(TraceLog log, IReadOnlySet<int> superStates, double? launchedAiLevel = null, int? subjectLocalWidth = null)
    {
        var notes = new List<string>();
        var frames = BehaviorCoordinates.Normalize(log, subjectLocalWidth, notes);
        var episodes = new List<BehaviorEpisode>();
        var incomplete = T.All.ToDictionary(t => t.Id, _ => 0);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        if (frames.Count == 0)
            return new Detection([], incomplete, ["frames"], ["The run recorded no match samples."], 0, 0, "unknown", null, false) { SubjectLocalWidth = subjectLocalWidth };

        void Need(string name, Func<FrameEvent, object?> get) { if (frames.All(f => get(f) is null)) missing.Add(name); }
        Need("positions (p1.x / p2.x)", f => f.P1.PosX is null || f.P2.PosX is null ? null : f.P1.PosX);
        Need("p2.stateType", f => f.P2.StateType);
        Need("p2.life", f => f.P2.Life);
        Need("p2.hitFall (Enemy Falling falls back to airborne hitstun while descending)", f => f.P2.HitFall);
        Need("p2.numProj (Projectile Response cannot be recognised)", f => f.P2.Projectiles);
        Need("backEdgeBodyDist (Cornered stays Unknown)", f => f.P1.BackEdgeBodyDist);
        Need("p1.aiLevel", f => f.P1.AiLevel);

        // Who controlled P1: the engine's own report when the build gives it, else the launch configuration.
        var levels = frames.Select(f => f.P1.AiLevel).OfType<double>().ToList();
        double? ai = levels.Count > 0 ? levels.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key : launchedAiLevel;
        var aiControlled = ai is > 0;
        var control = levels.Count > 0
            ? ai > 0 ? $"AI-controlled (AI level {F(ai!.Value)}, read from the engine)" : "not on the AI (the engine reported AI level 0)"
            : launchedAiLevel is { } la ? $"launched on AI level {F(la)} (this engine build does not report the AI level)" : "unknown";

        var rounds = SplitRounds(frames);
        foreach (var (round, f) in rounds)
        {
            if (f.Count < 10) continue;
            var t = new Track(f);
            KnockdownWindows(t, round, superStates, episodes, incomplete);
            GroundFollowUps(t, round, episodes);
            WakeUps(t, round, episodes);
            AntiAirs(t, round, episodes);
            Punishes(t, round, episodes, incomplete);
            Pressures(t, round, episodes);
            Blocks(t, round, episodes);
            Retreats(t, round, episodes);
            Supers(t, round, superStates, episodes);
            Projectiles(t, round, episodes, incomplete);
        }

        if (!aiControlled) notes.Add("P1 was not shown to be on the AI, so these episodes are not evidence about its AI.");
        return new Detection(episodes.OrderBy(e => e.StartFrame).ThenBy(e => e.Template, StringComparer.Ordinal).ToList(), incomplete,
            missing.ToList(), notes, rounds.Count, frames.Count, control, ai, aiControlled) { SubjectLocalWidth = subjectLocalWidth };
    }

    /// <summary>The run's samples exactly as <see cref="Detect"/> read them (same order, same coordinate scale) — for the timeline and Why.</summary>
    public static IReadOnlyList<FrameEvent> Samples(TraceLog log, Detection detection) => BehaviorCoordinates.Normalize(log, detection.SubjectLocalWidth);

    // ------------------------------------------------------------------ rounds and tracks

    private static List<(int Round, List<FrameEvent> Frames)> SplitRounds(IReadOnlyList<FrameEvent> frames)
    {
        var result = new List<(int, List<FrameEvent>)>();
        var current = new List<FrameEvent>();
        var number = frames[0].Round ?? 1;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var reset = i > 0 && ((f.Round is { } r && frames[i - 1].Round is { } pr && r != pr) ||
                                  (f.Round is null && f.P1.Life > frames[i - 1].P1.Life + 20 && f.P2.Life > frames[i - 1].P2.Life + 20));
            if (reset && current.Count > 0)
            {
                result.Add((number, current));
                current = [];
                number = f.Round ?? number + 1;
            }

            current.Add(f);
        }

        if (current.Count > 0) result.Add((number, current));
        return result;
    }

    internal sealed record Attack(int Start, int End, int? State);
    internal sealed record Movement(int Start, int End, double Covered, IReadOnlyList<int> States);

    internal sealed class Track
    {
        public Track(IReadOnlyList<FrameEvent> f)
        {
            F = f;
            P1Attacks = Attacks(true);
            P2Attacks = Attacks(false);
            Approaches = Moves(+1, T.ApproachMin);
            Away = Moves(-1, T.RetreatMin);
        }

        public IReadOnlyList<FrameEvent> F { get; }
        public int N => F.Count;
        public IReadOnlyList<Attack> P1Attacks { get; }
        public IReadOnlyList<Attack> P2Attacks { get; }
        public IReadOnlyList<Movement> Approaches { get; }
        public IReadOnlyList<Movement> Away { get; }
        public long Frame(int i) => F[i].Frame;
        public double? D(int i) => Distance(F[i]);
        public bool Down(int i) => Falling(F[i].P2) || Lying(F[i].P2);

        private List<Attack> Attacks(bool p1)
        {
            PlayerSample P(int i) => p1 ? F[i].P1 : F[i].P2;
            var list = new List<Attack>();
            int? start = null;
            for (var i = 0; i < N; i++)
            {
                var a = Attacking(P(i));
                var newState = start is not null && P(i).State != P(i - 1).State;
                if (a && (start is null || newState))
                {
                    if (start is { } s) list.Add(new Attack(s, i - 1, P(s).State));
                    start = i;
                }
                else if (!a && start is { } s2)
                {
                    list.Add(new Attack(s2, i - 1, P(s2).State));
                    start = null;
                }
            }

            if (start is { } last) list.Add(new Attack(last, N - 1, P(last).State));
            return list;
        }

        /// <summary>
        /// Stretches where P1 itself moves toward (+1) or away from (-1) P2 while free (not attacking, hit or guarding), covering at least <paramref name="min"/>.
        /// <para>
        /// Recorded positions are screen-relative (the engine measures x from the camera), so they also move when the camera scrolls. With velocity and distance
        /// recorded, each step is P1's own velocity that way (facing-relative Vel X, turned into a world direction), capped by how much the distance between
        /// the fighters actually changed — both camera-free. Being pushed or knocked back has no velocity of its own; walking into a body or a wall changes no
        /// distance. Without them, the position change is used, and a recorded velocity must still point that way.
        /// </para>
        /// </summary>
        private List<Movement> Moves(int sign, double min)
        {
            var list = new List<Movement>();
            int? start = null;
            var covered = 0.0;
            var gap = 0;
            var lastMoving = -1;
            var states = new List<int>();
            void Close()
            {
                if (start is { } s && covered >= min) list.Add(new Movement(s, lastMoving, covered, states.ToList()));
                start = null; covered = 0; gap = 0; states.Clear();
            }

            for (var i = 1; i < N; i++)
            {
                var a = F[i - 1];
                var b = F[i];
                if (a.P1.PosX is not { } x0 || b.P1.PosX is not { } x1 || a.P2.PosX is not { } o0) { Close(); continue; }
                var dir = Math.Sign(o0 - x0);
                if (dir == 0) dir = a.P1.Facing ?? 1;
                var free = !Attacking(b.P1) && !Hit(b.P1) && !Guarding(b.P1);
                double? velocity = b.P1.VelX is { } vx ? vx * (b.P1.Facing ?? 1) * dir * sign : null;
                var step = velocity is { } v && Distance(a) is { } d0 && Distance(b) is { } d1 ? Math.Min(v, (d0 - d1) * sign)
                    : velocity is <= 0.1 ? 0
                    : (x1 - x0) * dir * sign;
                if (free && step > 0.2)
                {
                    start ??= i - 1;   // the last sample before the fighter moved: where the approach started from
                    covered += step;
                    gap = 0;
                    lastMoving = i;
                    if (b.P1.State is { } st && (states.Count == 0 || states[^1] != st)) states.Add(st);
                }
                else if (start is not null && free && ++gap <= 2) { /* a short pause inside one approach */ }
                else Close();
            }

            Close();
            return list;
        }
    }

    /// <summary>
    /// What one attack did, read only from things that newly happened during it (so a lying or already-stunned opponent is not read as a hit): the defender
    /// lost life while not guarding, newly entered a hit state, or the attacker's MoveHit rose from zero → connected; the defender newly entered blockstun or
    /// MoveContact rose without MoveHit → blocked; none of these, with life readable and the attack over → whiffed; else unknown.
    /// </summary>
    internal static (string Outcome, int? At) OutcomeOf(IReadOnlyList<FrameEvent> f, Attack a, bool p1Attacks)
    {
        PlayerSample Att(int i) => p1Attacks ? f[i].P1 : f[i].P2;
        PlayerSample Def(int i) => p1Attacks ? f[i].P2 : f[i].P1;
        var last = Math.Min(a.End + 3, f.Count - 1);
        var lifeBefore = Def(Math.Max(0, a.Start - 1)).Life;
        var hitZero = Att(a.Start).MoveHit is 0;
        var contactZero = Att(a.Start).MoveContact is 0;
        int? blocked = null;
        for (var j = a.Start; j <= last; j++)
        {
            var d = Def(j);
            var prev = Def(Math.Max(0, j - 1));
            var at = Att(j);
            if (at.MoveHit is 0) hitZero = true;
            if (at.MoveContact is 0) contactZero = true;
            if (Blockstun(d) && !Blockstun(prev)) blocked ??= j;
            if (at.MoveContact > 0 && contactZero && at.MoveHit is 0) blocked ??= j;
            if (at.MoveHit > 0 && hitZero) return ("connected", j);
            if (j > a.Start && Hit(d) && !Hit(prev)) return ("connected", j);
            if (!Guarding(d) && lifeBefore is { } lb && d.Life is { } l && l < lb - 0.001) return ("connected", j);
        }

        if (blocked is { } b) return ("blocked", b);
        if (a.End >= f.Count - 2 || lifeBefore is null || Def(last).Life is null) return ("unknown", null);
        return ("whiffed", null);
    }

    /// <summary>Whether the defender (P1 when <paramref name="p1Defends"/>) was newly hit between two samples, read like <see cref="OutcomeOf"/>.</summary>
    private static bool NewlyHit(IReadOnlyList<FrameEvent> f, int from, int to, bool p1Defends)
    {
        PlayerSample Def(int i) => p1Defends ? f[i].P1 : f[i].P2;
        var lifeBefore = Def(Math.Max(0, from - 1)).Life;
        for (var j = from; j <= to && j < f.Count; j++)
        {
            var d = Def(j);
            if (j > 0 && Hit(d) && !Hit(Def(j - 1))) return true;
            if (!Guarding(d) && lifeBefore is { } lb && d.Life is { } l && l < lb - 0.001) return true;
        }

        return false;
    }

    /// <summary>
    /// The last sample that still answers the opponent's attack number <paramref name="k"/>: two samples after it ends, but never once their next attack has
    /// started (a guard or a hit there belongs to the next attack — the real KFM watch read one guard as blocking two attacks before this bound).
    /// </summary>
    private static int AnswerEnd(Track t, int k)
    {
        var b = t.P2Attacks[k];
        var next = k + 1 < t.P2Attacks.Count ? t.P2Attacks[k + 1].Start - 1 : t.N - 1;
        return Math.Max(b.Start, Math.Min(Math.Min(b.End + 2, next), t.N - 1));
    }

    private static List<Condition> RangeAt(Track t, int i)
    {
        var list = new List<Condition>();
        if (t.D(i) is { } d) list.Add(RangeOf(d));
        return list;
    }

    private static EpisodeStep OutcomeStep(Track t, Attack a, (string Outcome, int? At) o) =>
        new(t.Frame(o.At ?? a.End), StepKind.Outcome, o.Outcome);

    // ------------------------------------------------------------------ templates

    private static void KnockdownWindows(Track t, int round, IReadOnlySet<int> superStates, List<BehaviorEpisode> episodes, Dictionary<string, int> incomplete)
    {
        for (var s = 0; s < t.N; s++)
        {
            if (!t.Down(s) || (s > 0 && t.Down(s - 1))) continue;
            var e = s;
            while (e + 1 < t.N && !(t.F[e + 1].P2.Ctrl == true && !t.Down(e + 1))) e++;
            var limit = Math.Min(t.N - 1, e + T.ChaseGraceFrames);
            var found = false;
            var approached = false;
            foreach (var m in t.Approaches.Where(m => m.Start >= s && m.Start <= limit))
            {
                approached = true;
                var a = t.P1Attacks.FirstOrDefault(x => x.Start >= m.Start && x.Start <= Math.Min(limit, m.End + T.ChaseAttackWithin));
                if (a is null) continue;
                var o = OutcomeOf(t.F, a, true);
                var steps = new List<EpisodeStep>
                {
                    new(t.Frame(s), StepKind.Situation, Falling(t.F[s].P2) ? "enemy-falling" : "enemy-lying", t.F[s].P2.State)
                };
                var lie = Enumerable.Range(s, Math.Max(0, m.Start - s + 1)).FirstOrDefault(i => Lying(t.F[i].P2), -1);
                if (lie > s && Falling(t.F[s].P2)) steps.Add(new(t.Frame(lie), StepKind.Situation, "enemy-lying", t.F[lie].P2.State));
                steps.Add(new(t.Frame(m.Start), StepKind.Approach, "approach", m.States.FirstOrDefault()));
                steps.Add(new(t.Frame(m.End), StepKind.Distance, "distance", null, t.D(m.Start), t.D(m.End)));
                steps.Add(new(t.Frame(a.Start), StepKind.Attack, "attack", a.State, t.D(a.Start)));
                steps.Add(OutcomeStep(t, a, o));
                var conditions = new List<Condition> { Falling(t.F[s].P2) ? Condition.EnemyFalling : Condition.EnemyLying };
                if (lie > s) conditions.Add(Condition.EnemyLying);
                if (t.D(m.Start) is { } d0 && t.D(m.End) is { } d1 && d0 - d1 >= TrendMin) conditions.Add(Condition.DistanceDecreasing);
                if (t.F[m.Start].P1.Ctrl == true) conditions.Add(Condition.CanAct);
                conditions.AddRange(RangeAt(t, a.Start));
                if (o.Outcome == "connected") conditions.Add(Condition.MoveConnected);
                var notes = new List<string>();
                if (a.Start > e) notes.Add($"The attack started {a.Start - e} frame(s) after the opponent could act again (a meaty attack).");
                if (o.At is { } at && Lying(t.F[at].P2)) notes.Add("It connected while the opponent was still lying.");
                if (t.F[Math.Max(0, a.Start - 1)].P1.Power is { } pw0 && Enumerable.Range(a.Start, Math.Min(11, t.N - a.Start)).Select(i => t.F[i].P1.Power).OfType<double>().DefaultIfEmpty(pw0).Min() is var pwMin && pw0 - pwMin >= MeterLevel * 0.25)
                    notes.Add($"The follow-up spent {pw0 - pwMin:0} power.");
                var between = t.P1Attacks.Count(x => x.Start > s && x.Start < m.Start);
                if (between > 0) notes.Add($"Between the knockdown and the approach the fighter attacked {between} time(s) (follow-ups on the falling opponent).");
                episodes.Add(new BehaviorEpisode(T.KnockdownChase, round, t.Frame(s), t.Frame(o.At ?? a.End), steps, conditions.Distinct().ToList(),
                    m.States.Append(a.State ?? 0).Where(x => x != 0).Distinct().ToList(), o.Outcome, notes));
                found = true;
                break;
            }

            if (!found && approached) incomplete[T.KnockdownChase]++;
            s = e;
        }
    }

    /// <summary>One episode per time the opponent is on the ground: the first attack that takes life from them while they lie; later hits are noted.</summary>
    private static void GroundFollowUps(Track t, int round, List<BehaviorEpisode> episodes)
    {
        BehaviorEpisode? current = null;
        var currentLie = -1;
        var extra = 0;
        void Flush()
        {
            if (current is null) return;
            episodes.Add(extra == 0 ? current : current with { Notes = [$"{extra} more hit(s) while they were still down."] });
            current = null;
            extra = 0;
        }

        foreach (var a in t.P1Attacks)
        {
            var last = Math.Min(a.End + 3, t.N - 1);
            for (var j = Math.Max(1, a.Start); j <= last; j++)
            {
                var p2 = t.F[j].P2;
                var before = t.F[j - 1].P2;
                if (!(Lying(p2) || Lying(before)) || Guarding(p2) || p2.Life is not { } l || before.Life is not { } lb || l >= lb - 0.001) continue;
                var lie = j;
                while (lie > 0 && Lying(t.F[lie - 1].P2)) lie--;
                if (current is not null && lie == currentLie) { extra++; break; }   // the same time on the ground
                Flush();
                currentLie = lie;
                var start = Math.Min(lie, a.Start);
                current = new BehaviorEpisode(T.GroundFollowUp, round, t.Frame(start), t.Frame(j),
                [
                    new(t.Frame(lie), StepKind.Situation, "enemy-lying", t.F[lie].P2.State),
                    new(t.Frame(a.Start), StepKind.Attack, "attack", a.State, t.D(a.Start)),
                    new(t.Frame(j), StepKind.Outcome, "connected", null, lb, l, "while lying")
                ], [Condition.EnemyLying, Condition.MoveConnected], a.State is { } st ? [st] : [], "connected", []);
                break;
            }
        }

        Flush();
    }

    private static void WakeUps(Track t, int round, List<BehaviorEpisode> episodes)
    {
        for (var g = 1; g < t.N; g++)
        {
            var p2 = t.F[g].P2;
            var prev = t.F[g - 1].P2;
            // A get-up: the common get-up state, or leaving the ground without control and NOT in a hit state (an OTG hit that lifts them is a juggle).
            var gettingUp = (p2.State == 5120 && prev.State != 5120) || (!Lying(p2) && Lying(prev) && p2.Ctrl != true && !Hit(p2));
            if (!gettingUp || t.D(g) is not { } d || d >= CloseMax) continue;
            if (t.Approaches.Any(m => m.End >= g - 30 && m.End <= g)) continue;   // moving in is a Knockdown Chase
            var back = g;
            while (back + 1 < t.N && t.F[back].P2.Ctrl != true) back++;
            if (back - g > 90) continue;   // not a get-up: they were hit again or are still down
            var a = t.P1Attacks.FirstOrDefault(x => x.Start >= g - 10 && x.Start <= back + 8);
            if (a is null) continue;
            var o = OutcomeOf(t.F, a, true);
            var conditions = new List<Condition> { Condition.EnemyGettingUp, Condition.CloseRange };
            if (o.Outcome == "connected") conditions.Add(Condition.MoveConnected);
            episodes.Add(new BehaviorEpisode(T.WakeUpPressure, round, t.Frame(g), t.Frame(o.At ?? a.End),
            [
                new(t.Frame(g), StepKind.Situation, "enemy-getting-up", p2.State, d),
                new(t.Frame(a.Start), StepKind.Attack, "attack", a.State, t.D(a.Start), null, a.Start < back ? $"{back - a.Start} frame(s) before they could act" : $"{a.Start - back} frame(s) after they could act"),
                OutcomeStep(t, a, o)
            ], conditions, a.State is { } st ? [st] : [], o.Outcome, []));
            g = back;
        }
    }

    private static void AntiAirs(Track t, int round, List<BehaviorEpisode> episodes)
    {
        for (var s = 0; s < t.N; s++)
        {
            bool Jumping(int i) => Airborne(t.F[i].P2) && !Hit(t.F[i].P2);
            if (!Jumping(s) || (s > 0 && Jumping(s - 1))) continue;
            var e = s;
            while (e + 1 < t.N && Jumping(e + 1)) e++;
            if (e - s + 1 >= 3)
            {
                var a = t.P1Attacks.FirstOrDefault(x => x.Start >= s + 2 && x.Start <= e && !Airborne(t.F[x.Start].P1) && t.D(x.Start) is <= MediumMax);
                if (a is not null)
                {
                    var o = OutcomeOf(t.F, a, true);
                    var conditions = new List<Condition> { Condition.EnemyAirborne };
                    conditions.AddRange(RangeAt(t, a.Start));
                    if (o.Outcome == "connected") conditions.Add(Condition.MoveConnected);
                    episodes.Add(new BehaviorEpisode(T.AntiAir, round, t.Frame(s), t.Frame(o.At ?? a.End),
                    [
                        new(t.Frame(s), StepKind.Situation, "enemy-jump", t.F[s].P2.State, t.D(s)),
                        new(t.Frame(a.Start), StepKind.Attack, "attack", a.State, t.D(a.Start)),
                        OutcomeStep(t, a, o)
                    ], conditions, a.State is { } st ? [st] : [], o.Outcome, []));
                }
            }

            s = e;
        }
    }

    private static void Punishes(Track t, int round, List<BehaviorEpisode> episodes, Dictionary<string, int> incomplete)
    {
        for (var k = 0; k < t.P2Attacks.Count; k++)
        {
            var b = t.P2Attacks[k];
            var answered = AnswerEnd(t, k);
            if (NewlyHit(t.F, b.Start, answered, p1Defends: true)) continue;
            if (Attacking(t.F[b.Start].P1)) continue;   // both swinging: a trade, not a punish
            var from = b.Start + Math.Max(1, (b.End - b.Start) / 2);
            var a = t.P1Attacks.FirstOrDefault(x => x.Start >= from && x.Start <= b.End + 6 && t.F[x.Start].P2.Ctrl != true);
            if (a is null) continue;
            var o = OutcomeOf(t.F, a, true);
            if (o.Outcome != "connected") { incomplete[T.Punish]++; continue; }
            var guarded = Enumerable.Range(b.Start, answered - b.Start + 1).Any(i => Guarding(t.F[i].P1));
            episodes.Add(new BehaviorEpisode(T.Punish, round, t.Frame(b.Start), t.Frame(o.At ?? a.End),
            [
                new(t.Frame(b.Start), StepKind.Situation, "enemy-attack", b.State, t.D(b.Start), null, guarded ? "blocked" : "missed"),
                new(t.Frame(a.Start), StepKind.Attack, "attack", a.State, t.D(a.Start)),
                OutcomeStep(t, a, o)
            ], [Condition.EnemyAttacking, Condition.MoveConnected, .. RangeAt(t, a.Start)], a.State is { } st ? [st] : [], o.Outcome,
            [guarded ? "After blocking the opponent's attack." : "After the opponent's attack missed."]));
        }
    }

    private static void Pressures(Track t, int round, List<BehaviorEpisode> episodes)
    {
        for (var k = 0; k + 1 < t.P1Attacks.Count; k++)
        {
            var a1 = t.P1Attacks[k];
            var a2 = t.P1Attacks[k + 1];
            var o1 = OutcomeOf(t.F, a1, true);
            if (o1.Outcome != "blocked" || a2.Start > a1.End + T.PressureWithin) continue;
            var o2 = OutcomeOf(t.F, a2, true);
            var conditions = new List<Condition> { Condition.MoveBlocked };
            conditions.AddRange(RangeAt(t, a2.Start));
            if (o2.Outcome == "connected") conditions.Add(Condition.MoveConnected);
            episodes.Add(new BehaviorEpisode(T.Pressure, round, t.Frame(a1.Start), t.Frame(o2.At ?? a2.End),
            [
                new(t.Frame(a1.Start), StepKind.Attack, "attack", a1.State, t.D(a1.Start)),
                OutcomeStep(t, a1, o1),
                new(t.Frame(a2.Start), StepKind.Attack, "attack", a2.State, t.D(a2.Start), null, $"{a2.Start - a1.End - 1} frame(s) after the first ended"),
                OutcomeStep(t, a2, o2)
            ], conditions, new[] { a1.State, a2.State }.OfType<int>().Distinct().ToList(), o2.Outcome, []));
        }
    }

    private static void Blocks(Track t, int round, List<BehaviorEpisode> episodes)
    {
        for (var k = 0; k < t.P2Attacks.Count; k++)
        {
            var b = t.P2Attacks[k];
            var last = AnswerEnd(t, k);
            var guard = Enumerable.Range(b.Start, last - b.Start + 1).FirstOrDefault(i => Guarding(t.F[i].P1), -1);
            if (guard < 0 || NewlyHit(t.F, b.Start, last, p1Defends: true)) continue;
            episodes.Add(new BehaviorEpisode(T.Block, round, t.Frame(b.Start), t.Frame(Math.Max(guard, b.End)),
            [
                new(t.Frame(b.Start), StepKind.Situation, "enemy-attack", b.State, t.D(b.Start)),
                new(t.Frame(guard), StepKind.Guard, "guard", t.F[guard].P1.State),
                new(t.Frame(Math.Max(guard, b.End)), StepKind.Outcome, "blocked")
            ], [Condition.EnemyAttacking, .. RangeAt(t, b.Start)], t.F[guard].P1.State is { } st ? [st] : [], "blocked", []));
        }
    }

    private static void Retreats(Track t, int round, List<BehaviorEpisode> episodes)
    {
        foreach (var m in t.Away)
        {
            if (m.End - m.Start < 8) continue;
            var conditions = new List<Condition>();
            if (t.D(m.Start) is { } d0 && t.D(m.End) is { } d1 && d1 - d0 >= TrendMin) conditions.Add(Condition.DistanceIncreasing);
            if (Enumerable.Range(Math.Max(0, m.Start - 10), Math.Min(11, m.Start + 1)).Any(i => Attacking(t.F[i].P2))) conditions.Add(Condition.EnemyAttacking);
            conditions.AddRange(RangeAt(t, m.Start));
            if (Holds(Condition.Cornered, t.F, m.End) == true) conditions.Add(Condition.Cornered);
            episodes.Add(new BehaviorEpisode(T.Retreat, round, t.Frame(m.Start), t.Frame(m.End),
            [
                new(t.Frame(m.Start), StepKind.Retreat, "retreat", m.States.FirstOrDefault()),
                new(t.Frame(m.End), StepKind.Distance, "distance", null, t.D(m.Start), t.D(m.End))
            ], conditions, m.States, "n/a", []));
        }
    }

    private static void Supers(Track t, int round, IReadOnlySet<int> superStates, List<BehaviorEpisode> episodes)
    {
        long lastEnd = long.MinValue;
        foreach (var a in t.P1Attacks)
        {
            if (t.Frame(a.Start) <= lastEnd) continue;
            var before = t.F[Math.Max(0, a.Start - 1)].P1.Power;
            if (before is not { } p0 || p0 < MeterLevel) continue;
            var lowest = Enumerable.Range(a.Start, Math.Min(11, t.N - a.Start)).Select(i => t.F[i].P1.Power).OfType<double>().DefaultIfEmpty(p0).Min();
            var isSuper = a.State is { } s && superStates.Contains(s);
            // An attempt spends meter: some with the Super label, a full bar without it. A Super-labelled state that spends nothing is not counted.
            if (p0 - lowest < (isSuper ? MeterLevel * 0.5 : MeterLevel * 0.99)) continue;
            // A super can run through several states: judge the whole run of consecutive attack states.
            var end = a;
            foreach (var next in t.P1Attacks.Where(x => x.Start > a.Start))
            {
                if (next.Start > end.End + 1) break;
                end = next;
            }

            var span = new Attack(a.Start, end.End, a.State);
            var o = OutcomeOf(t.F, span, true);
            episodes.Add(new BehaviorEpisode(T.Super, round, t.Frame(a.Start), t.Frame(o.At ?? span.End),
            [
                new(t.Frame(Math.Max(0, a.Start - 1)), StepKind.Meter, "meter", null, p0),
                new(t.Frame(a.Start), StepKind.Attack, "attack", a.State, t.D(a.Start), null, isSuper ? "a Super ability's state" : $"spent {p0 - lowest:0} power"),
                OutcomeStep(t, span, o)
            ], [Condition.HasMeter, .. o.Outcome == "connected" ? new[] { Condition.MoveConnected } : []], a.State is { } st ? [st] : [], o.Outcome,
            isSuper ? [] : ["Recognised from the power spent, not from a Super category."]));
            lastEnd = t.Frame(span.End);
        }
    }

    private static void Projectiles(Track t, int round, List<BehaviorEpisode> episodes, Dictionary<string, int> incomplete)
    {
        for (var p = 1; p < t.N; p++)
        {
            if (t.F[p].P2.Projectiles is not { } n || t.F[p - 1].P2.Projectiles is not { } n0 || n <= n0) continue;
            var last = Math.Min(p + T.ProjectileWindow, t.N - 1);
            (int At, string Code, int? State)? response = null;
            for (var i = p; i <= last && response is null; i++)
            {
                var me = t.F[i].P1;
                var prev = t.F[i - 1].P1;
                if (Airborne(me) && !Airborne(prev) && !Hit(me)) response = (i, "jump", me.State);
                else if (Guarding(me) && !Guarding(prev)) response = (i, "guard", me.State);
                else if (t.P1Attacks.Any(a => a.Start == i)) response = (i, "attack", me.State);
                else if (t.Approaches.Any(m => m.Start == i)) response = (i, "approach", me.State);
                else if (t.Away.Any(m => m.Start == i)) response = (i, "retreat", me.State);
            }

            if (response is not { } r) { incomplete[T.ProjectileResponse]++; continue; }
            var hit = NewlyHit(t.F, p, last, p1Defends: true);
            episodes.Add(new BehaviorEpisode(T.ProjectileResponse, round, t.Frame(p), t.Frame(last),
            [
                new(t.Frame(p), StepKind.Situation, "enemy-projectile", t.F[p].P2.State, t.D(p)),
                new(t.Frame(r.At), r.Code switch { "jump" => StepKind.Jump, "guard" => StepKind.Guard, "attack" => StepKind.Attack, "retreat" => StepKind.Retreat, _ => StepKind.Approach }, r.Code, r.State),
                new(t.Frame(last), StepKind.Outcome, hit ? "hit" : "not-hit")
            ], [Condition.EnemyHasProjectile], r.State is { } st ? [st] : [], hit ? "hit" : "not-hit", []));
            p = last;
        }
    }
}
