using System.Security.Cryptography;
using System.Text;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Names;

/// <summary>
/// A content fingerprint of what a renameable object <em>is</em>, so a user name only stays on the object it was given to.
/// It never uses line numbers or comments (moving or commenting code keeps the name) and deliberately covers the identity of a
/// move rather than every controller in it: AI conditions edited inside a move's state do not invalidate its name, but a different
/// animation, state type, attack attribute or command does.
/// <list type="bullet">
/// <item>State: Statedef header parameters (type, movetype, physics, anim, ctrl, poweradd …) and the attack attributes of its HitDefs.</item>
/// <item>Ability: entry state number, its commands, and the entry state's fingerprint.</item>
/// <item>Command: the command steps, time and buffer.</item>
/// <item>Animation: the ordered sprite (group,index) of every frame.</item>
/// <item>Helper / projectile: its literal parameters and the states it runs.</item>
/// <item>Variable: the states whose controllers write it.</item>
/// </list>
/// </summary>
public static class NameFingerprint
{
    public static readonly IReadOnlySet<ObjectKind> RenameableKinds = new HashSet<ObjectKind>
    {
        ObjectKind.State, ObjectKind.Ability, ObjectKind.Command, ObjectKind.Animation, ObjectKind.Helper, ObjectKind.Projectile, ObjectKind.Variable
    };

    public static bool IsRenameable(SemanticObject? o) => o is not null && !o.IsStub && RenameableKinds.Contains(o.Kind);

    /// <summary>The fingerprint of <paramref name="id"/>, or null when it does not exist or cannot carry a user name.</summary>
    public static string? Compute(SemanticIndex index, string id)
    {
        var o = index.Get(id);
        if (!IsRenameable(o)) return null;
        var parts = new List<string> { o!.Kind.ToString() };
        switch (o.Kind)
        {
            case ObjectKind.State:
                parts.AddRange(StateParts(index, o));
                break;
            case ObjectKind.Ability:
                parts.Add("entry=" + o.Prop("entryState"));
                parts.Add("commands=" + o.Prop("commands"));
                if (o.Prop("entryState") is { } entry && index.Get(entry) is { } es) parts.AddRange(StateParts(index, es));
                break;
            case ObjectKind.Command:
                parts.Add("steps=" + Squash(o.Prop("steps")));
                parts.Add("time=" + o.Prop("time"));
                parts.Add("buffer=" + o.Prop("buffer"));
                break;
            case ObjectKind.Animation:
                foreach (var r in index.Outgoing(o.Id, RelationKind.Contains))
                    if (index.Get(r.To) is { Kind: ObjectKind.AnimFrame } frame) parts.Add($"{frame.Prop("group")},{frame.Prop("index")}");
                break;
            case ObjectKind.Helper or ObjectKind.Projectile:
                parts.AddRange(o.Props.Where(p => p.Key != "comment").Select(p => $"{p.Key}={p.Value}"));
                parts.AddRange(index.Outgoing(o.Id, RelationKind.HelperRunsState).Select(r => "runs=" + r.To).OrderBy(x => x, StringComparer.Ordinal));
                break;
            case ObjectKind.Variable:
                parts.AddRange(index.Incoming(o.Id, RelationKind.WritesVar, RelationKind.ResetsVar)
                    .Select(r => "writer=" + (index.OwnerState(r.From)?.Id ?? r.From)).Distinct().OrderBy(x => x, StringComparer.Ordinal));
                break;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return Convert.ToHexString(bytes)[..16];
    }

    private static IEnumerable<string> StateParts(SemanticIndex index, SemanticObject state)
    {
        foreach (var p in state.Props.Where(p => p.Key.StartsWith("p.", StringComparison.Ordinal)))
            yield return $"{p.Key}={Squash(p.Value)}";
        // Only this state's own controllers are visited, so review over a large character stays linear.
        var attrs = index.ControllersOf(state.Id)
            .Select(c => index.Get(c.Id + "/hitdef"))
            .OfType<SemanticObject>()
            .Select(h => "hit=" + Squash(h.Prop("p.attr") ?? "?"))
            .OrderBy(x => x, StringComparer.Ordinal);
        foreach (var a in attrs) yield return a;
    }

    private static string Squash(string? s) => s is null ? string.Empty : string.Concat(s.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
}
