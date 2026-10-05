using System.Globalization;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;

namespace IKEMENLab.Core.XRay.Director;

/// <summary>One line of the live view: "Enemy lying ✓", "Distance 82 px", "Chase limit 160 px ✓".</summary>
public sealed record WhyCheck(string Label, string Value, bool? Ok);

/// <summary>
/// Why at one moment of a Director test run. When the moment belongs to the generated behavior and its intention register is confirmed by what executed,
/// <see cref="CauseKnown"/> is true and <see cref="Statement"/> is an exact causal sentence. Otherwise it says so and claims nothing.
/// </summary>
public sealed record DirectorWhyAnswer(
    long Frame, bool Active, string? Phase, IReadOnlyList<WhyCheck> Checks, string? Action, string? Next, bool CauseKnown, string Statement, string? Verification,
    DirectorDecision? Decision);

/// <summary>
/// Exact "why" for Director-generated behavior: the intention register says which decision the generated code made, on which tick, with which distance
/// and enemy posture; the trace says what executed. Only when both agree is the cause stated. Existing AI keeps Phase 5's bounded Why
/// (<see cref="BehaviorWhy"/>, cause unknown).
/// </summary>
public static class DirectorWhy
{
    public const string NotGenerated = "This moment is not a decision of the generated behavior. For the character's existing AI the executing controller is not recorded, so no cause is claimed.";

    public static DirectorWhyAnswer Explain(SemanticIndex index, TaughtBehavior b, CharacterFacts facts, TraceLog log, long frame)
    {
        var s = b.Spec;
        var frames = BehaviorCoordinates.Normalize(log, facts.LocalCoord).ToList();
        if (frames.Count == 0) throw new InvalidOperationException("The run recorded no match samples.");
        var i = Math.Max(0, frames.FindLastIndex(f => f.Frame <= frame));
        var f0 = frames[i];
        var metrics = DirectorTesting.Evaluate("why", "why", "run", log, b, facts);
        // The latest decision at or before this moment (within the knockdown it belongs to).
        var decision = metrics.Decisions.LastOrDefault(d => d.Frame <= f0.Frame);
        // The follow-up by the name the user chose it under (Phase 1's ability name), not the raw state.
        var follow = string.IsNullOrWhiteSpace(s.FollowUpName) ? BehaviorText.State(index, s.FollowUpState) : s.FollowUpName;
        var px = Math.Abs(f0.Distance ?? double.NaN);
        string Posture(PlayerSample p) => p.State == DirectorCompiler.GetUpState ? "getting up" : BehaviorConditions.Lying(p) ? "lying" : BehaviorConditions.Falling(p) ? "falling" : Situation.Posture(p);
        var when = s.When switch { KnockdownWhen.Falling => "falling", KnockdownWhen.Lying => "lying", _ => "down" };
        var down = BehaviorConditions.Lying(f0.P2) || BehaviorConditions.Falling(f0.P2);

        // In the chase: the live view, and why it is running (the start decision, confirmed).
        if (f0.P1.State == facts.ChaseState)
        {
            var start = metrics.Decisions.LastOrDefault(d => d.Why == DirectorCompiler.WhyStart && d.Frame <= f0.Frame);
            var inRange = px <= s.AttackDistance;
            var checks = new List<WhyCheck>
            {
                new($"Enemy {Posture(f0.P2)}", down ? "yes" : "no (they are up)", down),
                new("Distance", double.IsNaN(px) ? "?" : $"{px:0} px", null),
                new("Chase limit", $"{s.ChaseLimit} px" + (start?.DistancePx is { } sd ? $" (started at {sd:0})" : string.Empty), start?.DistancePx is { } sd2 ? sd2 <= s.ChaseLimit + 0.5 : null),
                new("Attack distance", $"{s.AttackDistance} px", inRange ? true : null),   // reached ✓; not yet reached is no failure
                new("Give up", $"after {s.GiveUpFrames} frames", null)
            };
            var action = inRange && s.Timing == FollowUpTiming.AsTheyGetUp ? "Waiting in range for them to get up (the generated chase)" : "Walking toward them (the generated chase)";
            var verified = start is { Executed: true };
            return new DirectorWhyAnswer(f0.Frame, true, "chasing", checks, action, Then(s, follow),
                verified,
                verified ? $"Knockdown Chase is running because its start rule fired at frame {start!.Frame}: the enemy was {Down(start.Down)}, {start.DistancePx:0} px away " +
                           $"(chase limit {s.ChaseLimit} px), the fighter could act on the ground" + Roll(s) + "."
                         : "The fighter is in the generated chase state, but the start decision was not found in the intention register, so the cause is not claimed.",
                verified ? $"Intention register at frame {start!.Frame} (reason “start the chase”, next State {start.Next}); the fighter entered State {start.Next} — confirmed." : null,
                start);
        }

        // Right after a decision (the fighter is still in the state it chose): the exact reason for that decision.
        if (decision is not null && f0.Frame - decision.Frame <= 90 && decision.Executed && f0.P1.State == decision.Next)
        {
            var target = decision.Why == DirectorCompiler.WhyAttack ? follow : BehaviorText.State(index, decision.Next);
            var statement = decision.Why switch
            {
                DirectorCompiler.WhyAttack => $"{follow} chosen because the generated Knockdown Chase reached its attack-distance condition: {decision.DistancePx:0} px ≤ {s.AttackDistance} px" +
                                              (s.Timing == FollowUpTiming.AsTheyGetUp ? $", as the enemy was getting up ({s.LeadFrames} frame(s) before they could act, or just after)" : $", while the enemy was {Down(decision.Down)}") + ".",
                DirectorCompiler.WhyRecovered => $"{target} because the opponent recovered before the follow-up: the generated fallback is “{DirectorText.Fallback(s.Fallback)}”.",
                DirectorCompiler.WhyGiveUp => $"{target} because the generated Knockdown Chase gave up after {s.GiveUpFrames} frames without reaching {s.AttackDistance} px.",
                DirectorCompiler.WhyNoMeter => $"{target} because the follow-up was no longer valid (less than {s.MeterCost} power): the generated fallback is “{DirectorText.Fallback(s.Fallback)}”.",
                _ => $"{target} because the generated Knockdown Chase started (see the chase)."
            };
            return new DirectorWhyAnswer(f0.Frame, true, DirectorTesting.Reason(decision.Why), [new("Decision", DirectorTesting.Reason(decision.Why), true),
                    new("Distance at the decision", decision.DistancePx is { } dp ? $"{dp:0} px" : "?", decision.Why == DirectorCompiler.WhyAttack ? decision.DistancePx <= s.AttackDistance + 0.5 : null),
                    new("Enemy at the decision", Down(decision.Down), null)],
                $"{BehaviorText.CharacterName(index)} in {target}", null, true, statement,
                $"Intention register at frame {decision.Frame} (reason “{DirectorTesting.Reason(decision.Why)}”, next State {decision.Next}); the fighter entered State {decision.Next} — confirmed.",
                decision);
        }

        // A decision the trace does not confirm: say so, claim nothing.
        if (decision is { Executed: false } && f0.Frame - decision.Frame <= 30)
            return new DirectorWhyAnswer(f0.Frame, true, DirectorTesting.Reason(decision.Why), [], null, null, false,
                $"The generated behavior published “{DirectorTesting.Reason(decision.Why)} → State {decision.Next}” at frame {decision.Frame}, but the fighter went to State " +
                $"{decision.StateAfter?.ToString(CultureInfo.InvariantCulture) ?? "?"}: something else changed state, and which controller did is not recorded.",
                "Published intent was not executed — not a cause.", decision);

        return new DirectorWhyAnswer(f0.Frame, false, null, [], null, null, false, NotGenerated, null, null);
    }

    private static string Then(KnockdownChaseSpec s, string follow) =>
        follow + (s.Timing == FollowUpTiming.AsTheyGetUp ? " as they get up" : " while they are down");

    private static string Down(int code) => code switch { 2 => "lying (or getting up)", 1 => "in the air (falling)", _ => "up" };

    private static string Roll(KnockdownChaseSpec s) => s.Frequency == ChaseFrequency.Always ? string.Empty : $", and this knockdown's roll allowed it ({DirectorText.Frequency(s.Frequency)})";
}
