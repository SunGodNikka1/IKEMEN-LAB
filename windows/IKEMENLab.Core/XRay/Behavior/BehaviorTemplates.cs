namespace IKEMENLab.Core.XRay.Behavior;

/// <summary>
/// One recognisable behavior: its name and plain description, the conditions, actions and outcomes it is made of, and what it deliberately excludes.
/// The library is fixed and small (<see cref="BehaviorTemplates.All"/>); it is not mined from data.
/// </summary>
public sealed record BehaviorTemplate(
    string Id, string Name, string Category, string Summary, IReadOnlyList<Condition> Conditions, IReadOnlyList<string> Actions,
    IReadOnlyList<string> Outcomes, IReadOnlyList<string> Limitations, string Complete);

public static class BehaviorTemplates
{
    public const string KnockdownChase = "knockdown-chase", GroundFollowUp = "ground-follow-up", WakeUpPressure = "wakeup-pressure", AntiAir = "anti-air",
        Punish = "punish", Pressure = "pressure", Block = "defensive-block", Retreat = "retreat", Super = "super-attempt", ProjectileResponse = "projectile-response";

    /// <summary>Frames the fighter may still start the follow-up after the opponent got control back (a meaty / okizeme attack).</summary>
    public const int ChaseGraceFrames = 15;
    /// <summary>Frames between the end of the approach and the follow-up attack.</summary>
    public const int ChaseAttackWithin = 40;
    /// <summary>Minimum distance the fighter itself must cover toward the opponent for an approach (engine units).</summary>
    public const double ApproachMin = 15;
    public const double RetreatMin = 30;
    public const int PressureWithin = 20;
    public const int ProjectileWindow = 45;

    public static IReadOnlyList<BehaviorTemplate> All { get; } =
    [
        new(KnockdownChase, "Knockdown Chase", "Knockdown",
            "After knocking the opponent down, the fighter moves in and attacks before (or as) they get up.",
            [Condition.EnemyFalling, Condition.EnemyLying, Condition.DistanceDecreasing],
            ["moves toward the opponent (walk, run, dash — not an attack)", "starts an attack"],
            ["connected / blocked / whiffed (optional: the chase is complete without contact)"],
            [$"The approach must be the fighter's own movement (≥ {ApproachMin:0} units), not knockback or the opponent's bounce.",
             $"The attack must start while the opponent is still down or within {ChaseGraceFrames} frames of them getting control back.",
             "A knockdown followed by movement but no attack is incomplete and is not counted.",
             "Attacks whose own motion carries the fighter forward are not movement."],
            "knockdown → approach → attack"),
        new(GroundFollowUp, "Ground Follow-Up", "Knockdown",
            "An attack that hits the opponent while they are lying on the ground (an OTG hit).",
            [Condition.EnemyLying, Condition.MoveConnected],
            ["attacks the lying opponent"], ["connected while they were lying (required)"],
            ["Contact is read from the opponent losing life while lying; a hit from a projectile or helper cannot be told apart."],
            "opponent lying → attack → life lost while lying"),
        new(WakeUpPressure, "Wake-Up Pressure", "Knockdown",
            "The fighter waits close by and attacks as the opponent gets up.",
            [Condition.EnemyGettingUp, Condition.CloseRange],
            ["stays close (no chase)", "attacks around the moment they can act again"], ["connected / blocked / whiffed"],
            [$"Needs the fighter within {BehaviorConditions.CloseMax:0} units when the opponent starts getting up and no approach in the 30 frames before (that is a Knockdown Chase).",
             "Getting up is common state 5120 or leaving lying without control; custom get-up states are not recognised."],
            "opponent getting up → close → attack"),
        new(AntiAir, "Anti-Air", "Defense",
            "The opponent jumps and the fighter attacks them from the ground while they are still in the air.",
            [Condition.EnemyAirborne, Condition.MediumRange],
            ["attacks from the ground"], ["connected / blocked / whiffed"],
            [$"The opponent must be airborne on their own (not knocked up) for at least 3 frames, within {BehaviorConditions.MediumMax:0} units; the fighter must be on the ground when the attack starts."],
            "opponent jumps → grounded attack while they are airborne"),
        new(Punish, "Punish", "Defense",
            "The opponent's attack fails (missed or blocked) and the fighter hits them while they are still recovering.",
            [Condition.EnemyAttacking, Condition.MoveConnected],
            ["attacks during the opponent's recovery"], ["connected (required)"],
            ["The opponent's attack must not have hit the fighter; the fighter's attack must start in the second half of it or within 6 frames after, while the opponent has no control.",
             "An attempt that does not connect is not counted."],
            "opponent's attack fails → attack during recovery → hit"),
        new(Pressure, "Pressure Continuation", "Offense",
            "An attack is blocked and the fighter keeps attacking right away.",
            [Condition.MoveBlocked],
            ["a blocked attack", $"another attack within {PressureWithin} frames"], ["connected / blocked / whiffed"],
            ["Read from the opponent entering blockstun (150–159) or MoveContact rising without MoveHit."],
            "attack blocked → next attack quickly"),
        new(Block, "Defensive Response (Block)", "Defense",
            "The opponent attacks and the fighter guards it.",
            [Condition.EnemyAttacking],
            ["guards"], ["blocked (the fighter was not hit)"],
            ["Guarding is read from the common guard states 120–159; custom guard states are not recognised."],
            "opponent attacks → guard → not hit"),
        new(Retreat, "Retreat / Reposition", "Movement",
            "The fighter moves away from the opponent on purpose.",
            [Condition.DistanceIncreasing],
            [$"moves away (≥ {RetreatMin:0} units of its own movement while free)"], ["distance after"],
            ["Knockback, pushback while guarding and the opponent walking away are excluded (the fighter's own position must move away while it is free)."],
            "free → moves away"),
        new(Super, "Meter / Super Attempt", "Resources",
            "With meter available the fighter starts a super move.",
            [Condition.HasMeter],
            ["starts a move of a Super ability, or one that spends ≥ 1000 power"], ["connected / blocked / whiffed"],
            ["\"Super\" comes from X-Ray's static ability category (inferred) or a power drop of at least 1000 as the move starts."],
            "meter → super move"),
        new(ProjectileResponse, "Projectile Response", "Zoning",
            "The opponent throws a projectile and the fighter reacts (jumps, guards, moves or attacks).",
            [Condition.EnemyHasProjectile],
            [$"jumps, guards, moves or attacks within {ProjectileWindow} frames"], ["hit by it / not hit"],
            ["Only Projectile-controller projectiles are counted (NumProj); helper-based projectiles are invisible to it.",
             "Needs probe 0.4+ (NumProj); older traces leave it Unknown."],
            "opponent projectile → reaction")
    ];

    public static BehaviorTemplate Get(string id) => All.First(t => t.Id == id);
    public static BehaviorTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}
