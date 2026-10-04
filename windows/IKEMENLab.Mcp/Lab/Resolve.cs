using System.Globalization;
using System.Text.RegularExpressions;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Mcp.Protocol;

namespace IKEMENLab.Mcp.Lab;

/// <summary>
/// Turns what an agent says ("Kung Fu Palm", "1000", "ability:1000", "state:200", "Normal A") into a semantic id. Names are the display names the user
/// sees (their own Phase-1 names first, then X-Ray's), so a renamed ability is found by its new name. Ambiguity is an error listing the candidates;
/// nothing is guessed.
/// </summary>
internal static partial class Resolve
{
    public static string Ability(LoadedCharacter c, string spec)
    {
        var index = c.Index;
        var s = spec.Trim();
        if (index.Get(s) is { Kind: ObjectKind.Ability }) return s;
        if (Number(s) is { } n)
        {
            if (index.Get($"ability:{n}") is not null) return $"ability:{n}";
            var byEntry = AbilityWithEntry(index, $"state:{n}");
            if (byEntry is not null) return byEntry;
            throw new ToolError($"No ability starts at state {n}.", "list_abilities shows every ability with its id.");
        }

        if (s.StartsWith("state:", StringComparison.Ordinal))
            return AbilityWithEntry(index, s) ?? throw new ToolError($"No ability starts at {s}.", "explain_state describes any state; list_abilities lists the abilities.");

        return ByName(index, s, index.Of(ObjectKind.Ability).ToList(), "ability", "list_abilities shows every ability with its name and id.");
    }

    public static string State(LoadedCharacter c, string spec)
    {
        var index = c.Index;
        var s = spec.Trim();
        if (index.Get(s) is { Kind: ObjectKind.State }) return s;
        if (Number(s) is { } n)
            return index.Get($"state:{n}") is not null ? $"state:{n}" : throw new ToolError($"This character has no state {n}.");
        if (index.Get(s) is { Kind: ObjectKind.Ability } a && a.Prop("entryState") is { } entry) return entry;

        // A state's display name, or an ability's name (its entry state).
        var candidates = index.Of(ObjectKind.State).Where(o => !o.IsStub).ToList();
        var hit = TryByName(index, s, candidates, out var many);
        if (hit is not null) return hit;
        if (many.Count == 0)
        {
            var ability = TryByName(index, s, index.Of(ObjectKind.Ability).ToList(), out var abilities);
            if (ability is not null && index.Get(ability)?.Prop("entryState") is { } e) return e;
            many = abilities;
        }

        throw Ambiguous(index, s, many, "state", "Use the state number (for example 200) or its id (state:200).");
    }

    /// <summary>Any semantic object: an exact id (state:200, ability:1000, cmd:x, a controller id …), else an ability or state by number or name.</summary>
    public static string Object(LoadedCharacter c, string spec)
    {
        var s = spec.Trim();
        if (c.Index.Get(s) is not null) return s;
        if (Number(s) is { } n && c.Index.Get($"state:{n}") is not null) return $"state:{n}";
        try { return Ability(c, s); }
        catch (ToolError) { /* try states */ }
        return State(c, s);
    }

    private static string? AbilityWithEntry(SemanticIndex index, string stateId) =>
        index.Of(ObjectKind.Ability).Where(a => a.Prop("entryState") == stateId).Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();

    private static int? Number(string s) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string ByName(SemanticIndex index, string name, IReadOnlyList<SemanticObject> candidates, string kind, string hint) =>
        TryByName(index, name, candidates, out var many) ?? throw Ambiguous(index, name, many, kind, hint);

    /// <summary>Exact display name, then X-Ray's own name, then either without its "(notation)" tail, then a unique partial match.</summary>
    private static string? TryByName(SemanticIndex index, string name, IReadOnlyList<SemanticObject> candidates, out List<SemanticObject> many)
    {
        var want = Norm(name);
        many = [];
        foreach (var match in new Func<SemanticObject, bool>[]
                 {
                     o => Norm(index.NameOf(o.Id)) == want,
                     o => Norm(o.Name) == want,
                     o => Norm(StripTail(index.NameOf(o.Id))) == want || Norm(StripTail(o.Name)) == want,
                     o => Norm(index.NameOf(o.Id)).Contains(want, StringComparison.Ordinal) || Norm(o.Name).Contains(want, StringComparison.Ordinal)
                 })
        {
            var found = candidates.Where(match).ToList();
            if (found.Count == 1) return found[0].Id;
            if (found.Count > 1) { many = found; return null; }
        }

        return null;
    }

    private static ToolError Ambiguous(SemanticIndex index, string name, IReadOnlyList<SemanticObject> many, string kind, string hint) =>
        many.Count == 0
            ? new ToolError($"No {kind} named '{name}'.", hint)
            : new ToolError($"'{name}' matches {many.Count} {kind}s: " + string.Join("; ", many.Take(8).Select(o => $"{index.NameOf(o.Id)} ({o.Id})")) +
                            (many.Count > 8 ? " …" : string.Empty), "Pass the id or a more exact name.");

    private static string Norm(string s) => Spaces().Replace(s.Trim().ToLowerInvariant(), " ");

    private static string StripTail(string s) => Tail().Replace(s, string.Empty).Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*\([^()]*\)\s*$")]
    private static partial Regex Tail();
}
