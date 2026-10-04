using System.Globalization;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Sequences;

public enum SequenceActionKind { Ability, WalkForward, WalkBackward, Dash, Jump, Wait, Chase }

/// <summary>One step of a Sequence Lab sequence. Only the fields its <see cref="Kind"/> uses matter.</summary>
public sealed record SequenceAction
{
    public required SequenceActionKind Kind { get; init; }
    /// <summary>Ability: the ability to perform ("ability:1000").</summary>
    public string? AbilityId { get; init; }
    /// <summary>Walk forward / back and Wait: how many frames.</summary>
    public int Frames { get; init; }
    /// <summary>Chase: walk forward until the players are within this distance (engine units, the same as the approach distance).</summary>
    public int Distance { get; init; }
    /// <summary>Chase: stop the run as soon as the opponent has control again (they got up or recovered before you reached them).</summary>
    public bool StopIfOpponentRecovers { get; init; }

    public const int MaxFrames = 600, MaxDistance = 1000;

    public static SequenceAction Ability(string abilityId) => new() { Kind = SequenceActionKind.Ability, AbilityId = abilityId };
    public static SequenceAction Walk(int frames, bool forward = true) => new() { Kind = forward ? SequenceActionKind.WalkForward : SequenceActionKind.WalkBackward, Frames = frames };
    public static SequenceAction WaitFrames(int frames) => new() { Kind = SequenceActionKind.Wait, Frames = frames };
    public static SequenceAction ChaseTo(int distance, bool stopIfOpponentRecovers = false) => new() { Kind = SequenceActionKind.Chase, Distance = distance, StopIfOpponentRecovers = stopIfOpponentRecovers };
    public static SequenceAction Dash() => new() { Kind = SequenceActionKind.Dash };
    public static SequenceAction Jump() => new() { Kind = SequenceActionKind.Jump };

    /// <summary>Why this step's settings are unusable, or null.</summary>
    public string? Problem() => Kind switch
    {
        SequenceActionKind.Ability when string.IsNullOrWhiteSpace(AbilityId) => "Choose an ability.",
        SequenceActionKind.WalkForward or SequenceActionKind.WalkBackward or SequenceActionKind.Wait when Frames < 1 || Frames > MaxFrames => $"Frames must be 1–{MaxFrames}.",
        SequenceActionKind.Chase when Distance < 1 || Distance > MaxDistance => $"Distance must be 1–{MaxDistance}.",
        _ => null
    };
}

/// <summary>
/// A sequence the user builds in the Sequence Lab: an opening ability, then movement, waits, chases and more abilities. A saved sequence is a
/// variant; every edit gives it a new <see cref="Version"/>, and experiment results always name the exact version they ran.
/// </summary>
public sealed record Sequence(string Id, string Name, int Version, IReadOnlyList<SequenceAction> Actions)
{
    public string Key => $"{Id}@v{Version.ToString(CultureInfo.InvariantCulture)}";
    public string ScopeKey => ScopePrefix + Key;
    public const string ScopePrefix = "sequence:";

    public static string NewId() => "seq-" + Guid.NewGuid().ToString("N")[..10];
}

/// <summary>
/// The compact text form used by the CLI, QA scripts and tests: steps separated by <c>&gt;</c>. <c>1000</c> or <c>ability:1000</c> (ability),
/// <c>walk:20</c>, <c>back:20</c>, <c>dash</c>, <c>jump</c>, <c>wait:8</c>, <c>chase:35</c>, <c>chase:35!</c> (stop if the opponent recovers).
/// </summary>
public static class SequenceSpec
{
    public static IReadOnlyList<SequenceAction> Parse(string spec)
    {
        var actions = new List<SequenceAction>();
        foreach (var raw in spec.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            var head = parts[0].ToLowerInvariant();
            int Num(string what)
            {
                var text = parts.Length > 1 ? parts[1].TrimEnd('!') : string.Empty;
                return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"'{raw}': {what} must be a whole number.");
            }

            actions.Add(head switch
            {
                "ability" => SequenceAction.Ability("ability:" + (parts.Length > 1 ? parts[1] : throw new FormatException($"'{raw}' names no ability."))),
                "walk" => SequenceAction.Walk(Num("frames")),
                "back" => SequenceAction.Walk(Num("frames"), forward: false),
                "wait" => SequenceAction.WaitFrames(Num("frames")),
                "chase" => SequenceAction.ChaseTo(Num("distance"), raw.EndsWith('!')),
                "dash" => SequenceAction.Dash(),
                "jump" => SequenceAction.Jump(),
                _ when parts.Length == 1 && int.TryParse(head, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) => SequenceAction.Ability("ability:" + head),
                _ => throw new FormatException($"'{raw}' is not a step (ability:N, N, walk:N, back:N, wait:N, chase:D, chase:D!, dash, jump).")
            });
        }

        if (actions.Count == 0) throw new FormatException("The sequence is empty.");
        return actions;
    }

    public static string Format(IReadOnlyList<SequenceAction> actions) => string.Join(" > ", actions.Select(a => a.Kind switch
    {
        SequenceActionKind.Ability => a.AbilityId ?? "ability:?",
        SequenceActionKind.WalkForward => $"walk:{a.Frames}",
        SequenceActionKind.WalkBackward => $"back:{a.Frames}",
        SequenceActionKind.Wait => $"wait:{a.Frames}",
        SequenceActionKind.Chase => $"chase:{a.Distance}{(a.StopIfOpponentRecovers ? "!" : string.Empty)}",
        SequenceActionKind.Dash => "dash",
        _ => "jump"
    }));
}

/// <summary>Plain labels for steps, with the user's names (the Phase 1 resolver) for abilities. Ids stay secondary.</summary>
public static class SequenceLabels
{
    public static string Label(SequenceAction a, SemanticIndex? index) => a.Kind switch
    {
        SequenceActionKind.Ability => a.AbilityId is { } id && index?.Get(id) is not null ? index.NameOf(id) : a.AbilityId ?? "Ability",
        SequenceActionKind.WalkForward => $"Walk forward {a.Frames}f",
        SequenceActionKind.WalkBackward => $"Walk back {a.Frames}f",
        SequenceActionKind.Wait => $"Wait {a.Frames}f",
        SequenceActionKind.Chase => $"Chase until within {a.Distance}" + (a.StopIfOpponentRecovers ? " (stop if the opponent recovers)" : string.Empty),
        SequenceActionKind.Dash => "Dash",
        _ => "Jump"
    };

    public static string Chain(IEnumerable<SequenceAction> actions, SemanticIndex? index) => string.Join(" → ", actions.Select(a => Label(a, index)));
}
