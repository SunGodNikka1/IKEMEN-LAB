using System.Globalization;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Behavior;

/// <summary>The bounded, plain-language condition vocabulary behavior recognition speaks. Every entry says what runtime and static evidence backs it.</summary>
public enum Condition
{
    EnemyFalling, EnemyLying, EnemyGettingUp, EnemyAttacking, EnemyAirborne, EnemyInHitstun, EnemyBlocking, EnemyHasProjectile,
    CloseRange, MediumRange, LongRange, DistanceDecreasing, DistanceIncreasing,
    CanAct, HasMeter, LowLife, Cornered, EnemyCornered,
    MoveConnected, MoveBlocked, MoveWhiffed
}

/// <param name="Runtime">What in a recorded trace makes it true ("—" when it is never read from a trace).</param>
/// <param name="Static">Which literal trigger shapes in the character's AI are read as it ("—" when none).</param>
public sealed record ConditionInfo(Condition Id, string Name, string Meaning, string Runtime, string Static);

/// <summary>
/// The condition vocabulary: names, meanings, the thresholds used, how each is read from a trace (<see cref="Holds"/>) and which literal trigger shapes in
/// AI code are recognised as it (<see cref="FromTrigger"/>). Anything outside these shapes — a computed distance, a character-specific state, a variable —
/// stays Unknown. This is deliberately not a trigger solver.
/// </summary>
public static class BehaviorConditions
{
    /// <summary>Range bands in engine distance units (the research census bands: close &lt; 60, medium 60–160, long &gt; 160), on the 320-wide coordinate scale.</summary>
    public const double CloseMax = 60, MediumMax = 160;
    /// <summary>A body this close to the screen edge behind it is cornered.</summary>
    public const double CornerMax = 20;
    /// <summary>Low life: at or below this share of the life the round started with.</summary>
    public const double LowLifeShare = 0.3;
    public const double MeterLevel = 1000;
    /// <summary>"Distance decreasing / increasing": the distance changed by at least <see cref="TrendMin"/> over the last <see cref="TrendFrames"/> samples.</summary>
    public const int TrendFrames = 6;
    public const double TrendMin = 6;

    public static IReadOnlyList<ConditionInfo> All { get; } =
    [
        new(Condition.EnemyFalling, "Enemy Falling", "The opponent was knocked into a fall (airborne in a hit state).",
            "P2 in a hit state, airborne, with the engine's fall flag (HitFall); without that field, airborne hitstun while descending", "P2StateType = A with P2MoveType = H, EnemyNear,HitFall, P2StateNo 5050–5099"),
        new(Condition.EnemyLying, "Enemy Lying", "The opponent is lying on the ground.", "P2 StateType = L", "P2StateType = L, EnemyNear,StateType = L, P2StateNo 5100–5119"),
        new(Condition.EnemyGettingUp, "Enemy Getting Up", "The opponent is getting up from the ground and cannot act yet.",
            "P2 in common state 5120, or just left lying (L) without control", "P2StateNo = 5120, EnemyNear,StateNo = 5120"),
        new(Condition.EnemyAttacking, "Enemy Attacking", "The opponent is in an attack.", "P2 MoveType = A", "P2MoveType = A, EnemyNear,MoveType = A, InGuardDist"),
        new(Condition.EnemyAirborne, "Enemy Airborne", "The opponent is in the air on their own (jumping or air-attacking, not knocked up).",
            "P2 StateType = A and not in a hit state", "P2StateType = A (without P2MoveType = H)"),
        new(Condition.EnemyInHitstun, "Enemy in Hitstun", "The opponent is being hit.", "P2 MoveType = H or state 5000–5999, not guarding", "P2MoveType = H"),
        new(Condition.EnemyBlocking, "Enemy Blocking", "The opponent is guarding.", "P2 in a common guard state (120–159)", "P2StateNo 120–159"),
        new(Condition.EnemyHasProjectile, "Enemy Projectile", "The opponent has a projectile out.", "P2 NumProj > 0 (Projectile controller projectiles only)", "EnemyNear,NumProj > 0"),
        new(Condition.CloseRange, "Close Range", $"Within {CloseMax:0} units of the opponent.", $"|distance| < {CloseMax:0}", $"P2BodyDist X / P2Dist X < N with N ≤ {CloseMax:0}"),
        new(Condition.MediumRange, "Medium Range", $"{CloseMax:0}–{MediumMax:0} units from the opponent.", $"{CloseMax:0} ≤ |distance| ≤ {MediumMax:0}", $"P2BodyDist X / P2Dist X < N with {CloseMax:0} < N ≤ {MediumMax:0}"),
        new(Condition.LongRange, "Long Range", $"More than {MediumMax:0} units from the opponent.", $"|distance| > {MediumMax:0}", $"P2BodyDist X / P2Dist X > N with N ≥ {MediumMax:0}"),
        new(Condition.DistanceDecreasing, "Distance Decreasing", "The fighters are getting closer.", $"|distance| fell by ≥ {TrendMin:0} over {TrendFrames} samples", "—"),
        new(Condition.DistanceIncreasing, "Distance Increasing", "The fighters are moving apart.", $"|distance| grew by ≥ {TrendMin:0} over {TrendFrames} samples", "—"),
        new(Condition.CanAct, "Can Act", "The fighter has control.", "P1 Ctrl", "Ctrl"),
        new(Condition.HasMeter, "Has Meter", $"At least {MeterLevel:0} power.", $"P1 Power ≥ {MeterLevel:0}", $"Power ≥ N with N ≥ {MeterLevel:0}"),
        new(Condition.LowLife, "Low Life", $"At or below {LowLifeShare * 100:0}% of the round's starting life.", "P1 Life ≤ 30% of its first sample in the round",
            "Life < N with a literal N ≤ 300, or Life < LifeMax × k with k ≤ 0.3"),
        new(Condition.Cornered, "Cornered", "The fighter's back is near the screen edge.", $"P1 BackEdgeBodyDist ≤ {CornerMax:0}", "BackEdgeBodyDist / BackEdgeDist < N"),
        new(Condition.EnemyCornered, "Enemy Cornered", "The opponent's back is near the screen edge.", $"P2 BackEdgeBodyDist ≤ {CornerMax:0}", "EnemyNear,BackEdgeBodyDist < N, FrontEdgeBodyDist < N"),
        new(Condition.MoveConnected, "Move Connected", "The fighter's attack hit.", "during the attack: the opponent lost life, newly entered a hit state, or MoveHit rose", "MoveHit, MoveContact"),
        new(Condition.MoveBlocked, "Move Blocked", "The fighter's attack was guarded.", "during the attack: the opponent newly entered blockstun (150–159) or MoveContact rose without MoveHit", "MoveGuarded"),
        new(Condition.MoveWhiffed, "Move Whiffed", "The fighter's attack touched nothing.", "the attack ended with none of the above while life was readable", "—")
    ];

    public static ConditionInfo Info(Condition c) => All.First(x => x.Id == c);
    public static string Name(Condition c) => Info(c).Name;

    // ------------------------------------------------------------------ per-sample facts (shared with the detector)

    public static bool Hit(PlayerSample p) => RouteVerifier.InHitState(p) && !Guarding(p);
    public static bool Guarding(PlayerSample p) => p.State is >= 120 and < 160;
    public static bool Blockstun(PlayerSample p) => p.State is >= 150 and < 160;
    public static bool Lying(PlayerSample p) => string.Equals(p.StateType, "L", StringComparison.OrdinalIgnoreCase);
    public static bool Airborne(PlayerSample p) => string.Equals(p.StateType, "A", StringComparison.OrdinalIgnoreCase);
    public static bool Attacking(PlayerSample p) => string.Equals(p.MoveType, "A", StringComparison.OrdinalIgnoreCase);

    public static bool Falling(PlayerSample p) => Hit(p) && Airborne(p) && (p.HitFall == true || (p.HitFall is null && p.VelY > 0));

    /// <summary>
    /// Whether <paramref name="c"/> holds at sample <paramref name="i"/> of one round; null when the trace cannot say (a field the build did not expose, an
    /// attack outcome that is not a per-sample fact, the first samples of a trend).
    /// </summary>
    public static bool? Holds(Condition c, IReadOnlyList<FrameEvent> f, int i, double? roundStartLife = null)
    {
        var p1 = f[i].P1;
        var p2 = f[i].P2;
        var d = Distance(f[i]);
        switch (c)
        {
            case Condition.EnemyFalling: return p2.StateType is null ? null : Falling(p2);
            case Condition.EnemyLying: return p2.StateType is null ? null : Lying(p2);
            case Condition.EnemyGettingUp:
                if (p2.State is null && p2.StateType is null) return null;
                if (p2.State == 5120) return true;
                return !Lying(p2) && p2.Ctrl != true && Enumerable.Range(Math.Max(0, i - 3), Math.Min(3, i)).Any(k => Lying(f[k].P2));
            case Condition.EnemyAttacking: return p2.MoveType is null ? null : Attacking(p2);
            case Condition.EnemyAirborne: return p2.StateType is null ? null : Airborne(p2) && !Hit(p2);
            case Condition.EnemyInHitstun: return p2.MoveType is null && p2.State is null ? null : Hit(p2);
            case Condition.EnemyBlocking: return p2.State is null ? null : Guarding(p2);
            case Condition.EnemyHasProjectile: return p2.Projectiles is { } n ? n > 0 : null;
            case Condition.CloseRange: return d is { } a ? a < CloseMax : null;
            case Condition.MediumRange: return d is { } b ? b >= CloseMax && b <= MediumMax : null;
            case Condition.LongRange: return d is { } e ? e > MediumMax : null;
            case Condition.DistanceDecreasing:
            case Condition.DistanceIncreasing:
                if (i < TrendFrames || d is null || Distance(f[i - TrendFrames]) is not { } before) return null;
                return c == Condition.DistanceDecreasing ? before - d.Value >= TrendMin : d.Value - before >= TrendMin;
            case Condition.CanAct: return p1.Ctrl;
            case Condition.HasMeter: return p1.Power is { } pw ? pw >= MeterLevel : null;
            case Condition.LowLife: return p1.Life is { } l && roundStartLife is { } full && full > 0 ? l <= full * LowLifeShare : null;
            case Condition.Cornered: return p1.BackEdgeBodyDist is { } be ? be <= CornerMax : null;
            case Condition.EnemyCornered: return p2.BackEdgeBodyDist is { } be2 ? be2 <= CornerMax : null;
            default: return null;   // move outcomes belong to an attack, not to one sample
        }
    }

    /// <summary>The absolute distance between the fighters: the engine's P2DistX when recorded, else their position difference.</summary>
    public static double? Distance(FrameEvent f) =>
        f.Distance is { } d ? Math.Abs(d) : f.P1.PosX is { } a && f.P2.PosX is { } b ? Math.Abs(b - a) : null;

    public static Condition RangeOf(double distance) => distance < CloseMax ? Condition.CloseRange : distance <= MediumMax ? Condition.MediumRange : Condition.LongRange;

    // ------------------------------------------------------------------ static trigger shapes

    /// <summary>A condition recognised in one trigger, and whether it is required (a top-level conjunct) or only part of a larger expression (e.g. a weighting term).</summary>
    public sealed record TriggerCondition(Condition Condition, bool Required, string Text);

    /// <summary>
    /// The conditions literally present in one trigger expression. Only the shapes listed in <see cref="All"/> are read; a negated or non-literal comparison
    /// is ignored (it stays Unknown). <paramref name="required"/> says whether the expression is itself a required line (triggerall / a trigger group).
    /// </summary>
    public static IReadOnlyList<TriggerCondition> FromTrigger(Expr e)
    {
        var found = new List<TriggerCondition>();
        var conjuncts = ExprAnalyzer.Conjuncts(e).ToHashSet(ReferenceEqualityComparer.Instance);
        Visit(e, negated: false);
        return found;

        void Add(Condition c, Expr at)
        {
            var text = ExprPrinter.ToSExpr(at);
            if (found.All(t => t.Text != text || t.Condition != c)) found.Add(new TriggerCondition(c, conjuncts.Contains(at), text));
        }

        void Visit(Expr x, bool negated)
        {
            switch (x)
            {
                case Unary { Op: "!" } u: Visit(u.Operand, !negated); return;
                case Binary b when b.Op is "&&" or "||" or "^^": Visit(b.Left, negated); Visit(b.Right, negated); return;
                case Binary b when Atom(b) is { } c: if (!negated) Add(c, b); return;
                case Binary b: Visit(b.Left, negated); Visit(b.Right, negated); return;   // arithmetic (weighting terms) and comparisons of them
                case Call c: foreach (var a in c.Args) Visit(a, negated); return;      // ifelse(cond, a, b), cond(...)
                case Unary u2: Visit(u2.Operand, negated); return;
                case Ident { Name: "inguarddist" } when !negated: Add(Condition.EnemyAttacking, x); return;
                case Ident { Name: "movehit" or "movecontact" } when !negated: Add(Condition.MoveConnected, x); return;
                case Ident { Name: "moveguarded" } when !negated: Add(Condition.MoveBlocked, x); return;
                case Ident { Name: "ctrl" } when !negated: Add(Condition.CanAct, x); return;
                case Redirect { Kind: "enemynear" or "enemy" or "p2", Inner: Ident { Name: "hitfall" } } when !negated: Add(Condition.EnemyFalling, x); return;
            }
        }
    }

    private static string? Subject(Expr left, out bool enemy)
    {
        enemy = false;
        switch (left)
        {
            case Ident i: return i.Name;
            case Call c: return c.Param is null ? c.Name : c.Name + " " + c.Param;
            case Redirect { Kind: "enemynear" or "enemy" or "p2" } r:
                enemy = true;
                return r.Inner switch { Ident ii => ii.Name, Call cc => cc.Param is null ? cc.Name : cc.Name + " " + cc.Param, _ => null };
            default: return null;
        }
    }

    private static double? Number(Expr e) => e switch
    {
        NumberLit n => n.Value,
        Unary { Op: "-", Operand: NumberLit m } => -m.Value,
        _ => null
    };

    private static Condition? Atom(Binary b)
    {
        var name = Subject(b.Left, out var enemy);
        if (name is null) return null;
        var op = b.Op;
        var word = b.Right is Ident w ? w.Name : null;
        var n = Number(b.Right);

        // P2 / EnemyNear posture.
        if ((name == "p2statetype" || (enemy && name == "statetype")) && op == "=")
            return word switch { "l" => Condition.EnemyLying, "a" => Condition.EnemyAirborne, _ => null };
        if ((name == "p2movetype" || (enemy && name == "movetype")) && op == "=")
            return word switch { "a" => Condition.EnemyAttacking, "h" => Condition.EnemyInHitstun, _ => null };
        if (enemy && name == "hitfall" && op is "=" or "!=" && n is { } hf) return (op == "=") == (hf != 0) ? Condition.EnemyFalling : null;
        if (name == "p2stateno" || (enemy && name == "stateno"))
        {
            static Condition? Band(double lo, double hi) =>
                lo >= 5120 && hi <= 5120 ? Condition.EnemyGettingUp
                : lo >= 5100 && hi < 5120 ? Condition.EnemyLying
                : lo >= 5050 && hi < 5100 ? Condition.EnemyFalling
                : lo >= 120 && hi < 160 ? Condition.EnemyBlocking
                : null;
            if (op == "=" && n is { } s) return Band(s, s);
            if (op == "=" && b.Right is Interval { Low: var lo, High: var hi } && Number(lo) is { } l && Number(hi) is { } h) return Band(l, h);
            if (op is "<" or "<=" && n is { } u && u <= 5120 && u > 5100) return null;   // "stateno < 5120" alone says nothing about lying
            return null;
        }

        if (name == "inguarddist" && op == "=" && n is 1) return Condition.EnemyAttacking;
        if ((enemy && name == "numproj") || name == "p2numproj") return op is ">" or ">=" or "!=" ? Condition.EnemyHasProjectile : null;

        // Distance bands: only "closer than N" / "farther than N" with a literal N.
        if (name is "p2bodydist x" or "p2dist x" || (enemy && name is "p2bodydist x" or "p2dist x"))
        {
            if (n is not { } dist) return null;
            if (op is "<" or "<=") return dist <= CloseMax ? Condition.CloseRange : dist <= MediumMax ? Condition.MediumRange : null;
            if (op is ">" or ">=") return dist >= MediumMax ? Condition.LongRange : null;
            return null;
        }

        if (name is "backedgebodydist" or "backedgedist" && op is "<" or "<=" && n is { } be && be <= 60)
            return enemy ? Condition.EnemyCornered : Condition.Cornered;
        if (!enemy && name is "frontedgebodydist" or "frontedgedist" && op is "<" or "<=" && n is { } fe && fe <= 60) return Condition.EnemyCornered;
        if (!enemy && name == "power" && op is ">=" or ">" && n is { } pw && pw >= MeterLevel) return Condition.HasMeter;
        if (!enemy && name == "life" && op is "<" or "<=" && LowLifeBound(b.Right)) return Condition.LowLife;
        if (!enemy && name == "ctrl" && op == "=" && n is 1) return Condition.CanAct;
        if (!enemy && name is "movecontact" or "movehit" && op is ">" or ">=" or "=" && n is >= 1) return Condition.MoveConnected;
        if (!enemy && name == "moveguarded" && op is ">" or ">=" or "=" && n is >= 1) return Condition.MoveBlocked;
        return null;
    }

    /// <summary>
    /// "Life &lt; N" is Low Life only when N is at most <see cref="LowLifeShare"/> of the life: a literal N ≤ 300 (the default 1000-life scale) or
    /// LifeMax × k with k ≤ 0.3. Anything else ("Life &lt; 900", a variable, a computed value) stays Unknown.
    /// </summary>
    private static bool LowLifeBound(Expr right) => right switch
    {
        _ when Number(right) is { } n => n <= LowLifeShare * 1000,
        Binary { Op: "*", Left: Ident { Name: "lifemax" }, Right: var k } => Number(k) is { } f && f <= LowLifeShare,
        Binary { Op: "*", Left: var k, Right: Ident { Name: "lifemax" } } => Number(k) is { } f && f <= LowLifeShare,
        Binary { Op: "/", Left: Ident { Name: "lifemax" }, Right: var k } => Number(k) is { } f && f >= 1 / LowLifeShare,
        _ => false
    };

    internal static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
