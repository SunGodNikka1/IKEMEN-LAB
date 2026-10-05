using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IKEMENLab.Core.XRay.Director;

/// <summary>Which knockdown starts the behavior.</summary>
public enum KnockdownWhen { Falling, Lying, Both }

/// <summary>When the follow-up is started once the fighter is within the attack distance.</summary>
public enum FollowUpTiming
{
    /// <summary>At once, while the opponent is still down (falling, lying or getting up) — for a move that can hit a downed opponent.</summary>
    WhileDown,
    /// <summary>As they get up: <see cref="KnockdownChaseSpec.LeadFrames"/> frames before they can act again, or just after (a meaty attack).</summary>
    AsTheyGetUp
}

/// <summary>How often the behavior takes a knockdown it could take. Rolled ONCE per knockdown (never a per-tick lottery).</summary>
public enum ChaseFrequency { Always, Often, Sometimes }

/// <summary>What the fighter does when the opponent recovers before the follow-up (or the follow-up is no longer valid).</summary>
public enum ChaseFallback
{
    /// <summary>Stop (stand, with control) and let the rest of the AI decide again.</summary>
    StopAndReassess,
    /// <summary>Raise guard (the common guard-start state).</summary>
    Block,
    /// <summary>Walk back briefly, then stand with control.</summary>
    Retreat
}

/// <summary>Where the generated decision goes relative to the character's existing State -1 rules.</summary>
public enum OwnershipPlacement
{
    /// <summary>Before every existing rule: the behavior owns the knockdown whenever it applies.</summary>
    BeforeExisting,
    /// <summary>After every existing rule: the existing rules keep priority; the behavior acts only when none of them changes state first.</summary>
    AfterExisting,
    /// <summary>Before every existing rule, and the listed existing rules yield while the opponent is down (they are suppressed for knockdowns only).</summary>
    SuppressSpecific
}

/// <summary>A taught behavior's lifecycle. Draft and Testing never alter the installed character; only Live has been deployed.</summary>
public enum TaughtStatus { Draft, Testing, ReadyForApproval, Live, Retired }

/// <summary>
/// The semantic Knockdown Chase the user taught: plain choices, distances in X-Ray's one distance scale (320-wide world units, "px"), the follow-up by
/// ability, the fallback and the ownership placement. The generated code is a pure function of this (plus the character's own facts); it is never
/// hand-edited.
/// </summary>
public sealed record KnockdownChaseSpec(
    KnockdownWhen When, int ChaseLimit, int AttackDistance, FollowUpTiming Timing, int LeadFrames, int GiveUpFrames, ChaseFrequency Frequency,
    string FollowUpAbilityId, int FollowUpState, string FollowUpName, ChaseFallback Fallback, int MeterCost,
    OwnershipPlacement Placement, IReadOnlyList<string> Suppress)
{
    public const int MinDistance = 1, MaxDistance = 600, MinGiveUp = 10, MaxGiveUp = 600, MaxLead = 30, GraceFrames = 15;

    /// <summary>Why the choices cannot be compiled, in plain words (empty = they can).</summary>
    public IReadOnlyList<string> Problems()
    {
        var list = new List<string>();
        if (AttackDistance < MinDistance || AttackDistance > MaxDistance) list.Add($"The attack distance must be {MinDistance}–{MaxDistance} px.");
        if (ChaseLimit < MinDistance || ChaseLimit > MaxDistance) list.Add($"The chase limit must be {MinDistance}–{MaxDistance} px.");
        if (ChaseLimit < AttackDistance) list.Add("The chase limit must be at least the attack distance.");
        if (GiveUpFrames < MinGiveUp || GiveUpFrames > MaxGiveUp) list.Add($"Give up after {MinGiveUp}–{MaxGiveUp} frames.");
        if (LeadFrames < 0 || LeadFrames > MaxLead) list.Add($"The wake-up lead must be 0–{MaxLead} frames.");
        if (string.IsNullOrWhiteSpace(FollowUpAbilityId)) list.Add("Choose the follow-up move.");
        if (Placement == OwnershipPlacement.SuppressSpecific && Suppress.Count == 0) list.Add("Choose the existing rule(s) to suppress, or another placement.");
        return list;
    }

    /// <summary>The per-knockdown roll threshold out of 1000 (Random is 0–999).</summary>
    public int FrequencyThreshold => Frequency switch { ChaseFrequency.Often => 700, ChaseFrequency.Sometimes => 350, _ => 1000 };
}

/// <summary>Where a behavior was taught from (pre-filled from evidence, not typed in).</summary>
/// <param name="Kind">"experiment" (a Sequence Lab experiment), "episode" (a recognised Knockdown Chase in a watched run) or "ability" (Ability Lab).</param>
public sealed record TaughtSource(string Kind, string Id, string Text);

/// <summary>The controlled test the behavior runs before promotion: the proven opening that knocks the opponent down, then the AI takes over.</summary>
/// <param name="Opening">Ability ids played by the driver from neutral (the knockdown), before P1 is handed to its AI.</param>
/// <param name="SourceSequenceSpec">The Sequence Lab sequence the behavior was taught from (re-run as a regression), when there is one.</param>
public sealed record TaughtTestPlan(IReadOnlyList<string> Opening, string? SourceSequenceSpec, string? SecondOpponent, int Trials = 2, int WatchFrames = 420);

/// <summary>The approval of one exact generated revision. Anything that changes the build makes it void.</summary>
public sealed record ApprovalRecord(DateTime ApprovedUtc, string By, string BuildHash, string DiffHash, string ModelHash, string TestId, string Summary);

/// <summary>A one-way deployment: the SafeMutation operations (in order), the manifest, and the hashes the install must have afterwards.</summary>
public sealed record DeploymentRecord(
    string Id, DateTime DeployedUtc, string IkemenRoot, string CharacterFolder, string BuildHash, string ManifestPath, IReadOnlyList<string> OperationIds,
    IReadOnlyDictionary<string, string> DeployedHashes, bool RolledBack = false);

/// <summary>A taught AI behavior (v1: the Knockdown Chase family only).</summary>
public sealed record TaughtBehavior(
    string Id, string Family, string Character, string CharacterFolder, int Revision, TaughtStatus Status, KnockdownChaseSpec Spec, TaughtSource Source,
    TaughtTestPlan Tests, DateTime CreatedUtc, DateTime UpdatedUtc)
{
    public const string SchemaVersion = "ikemenlab.director.behavior/1";
    public const string KnockdownChaseFamily = "knockdown-chase";
    /// <summary>The intention-register code of this behavior (xrd_beh). v1 has one Director behavior per character.</summary>
    public const int Slot = 1;

    public string Name => "Knockdown Chase";
    public ApprovalRecord? Approval { get; init; }
    public DeploymentRecord? Deployment { get; init; }
    /// <summary>The latest test report (any outcome) and the latest passing one.</summary>
    public string? LastTestId { get; init; }
    public string? PassedTestId { get; init; }

    public static string NewId() => "kc-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>The identity of the semantic model the code is generated from (not of when or by whom).</summary>
    public string ModelHash => DirectorHash.Of(
        $"{Family}|{Slot}|{Spec.When}|{Spec.ChaseLimit}|{Spec.AttackDistance}|{Spec.Timing}|{Spec.LeadFrames}|{Spec.GiveUpFrames}|{Spec.Frequency}|" +
        $"{Spec.FollowUpAbilityId}|{Spec.FollowUpState}|{Spec.FollowUpName}|{Spec.Fallback}|{Spec.MeterCost}|{Spec.Placement}|{string.Join(",", Spec.Suppress)}");
}

public static class DirectorHash
{
    public static string Of(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string OfBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}

/// <summary>Plain words for the model — the simple card ("WHEN / DO / THEN / GIVE UP IF").</summary>
public static class DirectorText
{
    public static string When(KnockdownWhen w) => w switch
    {
        KnockdownWhen.Falling => "Enemy is falling (knocked down, still in the air)",
        KnockdownWhen.Lying => "Enemy is lying on the ground",
        _ => "Enemy is knocked down (falling or lying)"
    };

    public static string Do(KnockdownChaseSpec s) => $"Chase until {s.AttackDistance} px (only when the knockdown is within {s.ChaseLimit} px)";

    public static string Then(KnockdownChaseSpec s) => s.FollowUpName + (s.Timing == FollowUpTiming.AsTheyGetUp
        ? s.LeadFrames > 0 ? $" as they get up ({s.LeadFrames} frame{(s.LeadFrames == 1 ? string.Empty : "s")} before they can act)" : " as they get up"
        : " while they are still down") + (s.MeterCost > 0 ? $" — only with {s.MeterCost} power" : string.Empty);

    public static string GiveUp(KnockdownChaseSpec s) =>
        $"Opponent recovers or {s.GiveUpFrames} frames pass — then {Fallback(s.Fallback).ToLowerInvariant()}";

    public static string Fallback(ChaseFallback f) => f switch
    {
        ChaseFallback.Block => "Block",
        ChaseFallback.Retreat => "Retreat",
        _ => "Stop and reassess"
    };

    public static string Frequency(ChaseFrequency f) => f switch
    {
        ChaseFrequency.Often => "Often (7 knockdowns in 10)",
        ChaseFrequency.Sometimes => "Sometimes (about 1 knockdown in 3)",
        _ => "Always"
    };

    public static string Placement(OwnershipPlacement p) => p switch
    {
        OwnershipPlacement.AfterExisting => "After the existing AI (existing rules keep priority)",
        OwnershipPlacement.SuppressSpecific => "Before the existing AI, and the chosen existing rules yield on knockdowns",
        _ => "Before the existing AI (this behavior owns the knockdown when it applies)"
    };

    public static string Status(TaughtStatus s) => s switch
    {
        TaughtStatus.ReadyForApproval => "Ready for approval",
        _ => s.ToString()
    };

    /// <summary>The whole behavior in one plain paragraph.</summary>
    public static string Sentence(TaughtBehavior b) =>
        $"When {When(b.Spec.When).ToLowerInvariant()} within {b.Spec.ChaseLimit} px, {b.CharacterFolder} walks toward them until {b.Spec.AttackDistance} px, " +
        $"then uses {Then(b.Spec)}. {Frequency(b.Spec.Frequency)}. If the opponent recovers first or {b.Spec.GiveUpFrames} frames pass, " +
        $"{Fallback(b.Spec.Fallback).ToLowerInvariant()}.";

    internal static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
