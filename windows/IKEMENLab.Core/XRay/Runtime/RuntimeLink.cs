using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Runtime;

/// <summary>A runtime state number tied (or not) to the static State object that defines it.</summary>
public sealed record RuntimeStateLink(int Player, int StateNo, string? ObjectId, long FirstFrame, int Frames, bool IsCommonState);

public sealed record RuntimeAnimLink(int Player, int AnimNo, string? ObjectId, long FirstFrame, int Frames);

/// <summary>
/// Runtime facts joined to the static index. These links are evidence pointers, deliberately kept apart from the index's
/// relationships: milestone 3 will promote matching edges to <see cref="Confidence.RuntimeVerified"/>; milestone 1 never does.
/// </summary>
public sealed class RuntimeEvidence
{
    public required IReadOnlyList<RuntimeStateLink> States { get; init; }
    public required IReadOnlyList<RuntimeAnimLink> Animations { get; init; }

    public IEnumerable<RuntimeStateLink> Resolved => States.Where(s => s.ObjectId is not null);
    public IEnumerable<RuntimeStateLink> Unresolved => States.Where(s => s.ObjectId is null);
}

public static class RuntimeLink
{
    /// <summary>
    /// Ties the subject player's runtime states and animations to the static objects. Only <paramref name="subjectPlayer"/> is
    /// linked: the other side is a dummy whose files are not in this index.
    /// </summary>
    public static RuntimeEvidence Associate(SemanticIndex index, TraceLog log, int subjectPlayer = 1)
    {
        var states = new Dictionary<int, (long First, int Frames)>();
        var anims = new Dictionary<int, (long First, int Frames)>();
        foreach (var f in log.Frames)
        {
            var p = subjectPlayer == 1 ? f.P1 : f.P2;
            if (p.State is { } s) states[s] = states.TryGetValue(s, out var e) ? (e.First, e.Frames + 1) : (f.Frame, 1);
            if (p.Anim is { } a) anims[a] = anims.TryGetValue(a, out var e) ? (e.First, e.Frames + 1) : (f.Frame, 1);
        }

        // A state_change names states even when no frame sampled them (a one-tick state).
        foreach (var c in log.Events.OfType<StateChangeEvent>().Where(c => c.Player == subjectPlayer))
            foreach (var s in new[] { c.From, c.To }.OfType<int>())
                if (!states.ContainsKey(s)) states[s] = (c.Frame, 0);

        return new RuntimeEvidence
        {
            States = states.OrderBy(kv => kv.Value.First).ThenBy(kv => kv.Key).Select(kv =>
            {
                var obj = index.Get($"state:{kv.Key}");
                var resolved = obj is { IsStub: false } ? obj : null;
                return new RuntimeStateLink(subjectPlayer, kv.Key, resolved?.Id, kv.Value.First, kv.Value.Frames, resolved?.Prop("common") == "true");
            }).ToList(),
            Animations = anims.OrderBy(kv => kv.Value.First).ThenBy(kv => kv.Key).Select(kv =>
            {
                var obj = index.Get($"anim:{kv.Key}");
                return new RuntimeAnimLink(subjectPlayer, kv.Key, obj is { IsStub: false } ? obj.Id : null, kv.Value.First, kv.Value.Frames);
            }).ToList()
        };
    }
}
