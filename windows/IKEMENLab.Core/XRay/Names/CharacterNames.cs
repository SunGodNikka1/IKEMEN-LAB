using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Names;

/// <summary>
/// User names for one character: loads the app-owned overlay, attaches a <see cref="SemanticNames"/> resolver to the index, and
/// applies rename / reset / review actions (each saved immediately, then the resolver is rebuilt and re-attached).
/// </summary>
public sealed class CharacterNames
{
    private readonly NameOverlayStore _store;
    private readonly Func<DateTime> _clock;
    private NameOverlayDocument _doc;

    private CharacterNames(NameOverlayStore store, SemanticIndex index, NameOverlayDocument doc, Func<DateTime>? clock)
    {
        _store = store;
        Index = index;
        _doc = doc;
        _clock = clock ?? (() => DateTime.UtcNow);
        Apply();
    }

    public SemanticIndex Index { get; }
    public SemanticNames Names { get; private set; } = null!;
    public string StorePath => _store.PathFor(_doc.CharacterFolder);

    /// <summary>Loads the names for <paramref name="characterFolder"/> and attaches them to <paramref name="index"/>.</summary>
    public static CharacterNames Load(NameOverlayStore store, SemanticIndex index, string characterFolder, Func<DateTime>? clock = null) =>
        new(store, index, store.Load(characterFolder), clock);

    /// <summary>Gives <paramref name="id"/> a user name. Returns an error (and changes nothing) when it cannot.</summary>
    public string? Rename(string id, string label)
    {
        if (!Names.CanRename(id)) return Index.Get(id) is null ? $"'{id}' does not exist." : $"{Names.Default(id)} cannot be renamed.";
        if (SemanticNames.ValidateLabel(label) is { } problem) return problem;
        _doc.Names[id] = Names.NewRecord(id, label, _clock());
        return Commit();
    }

    /// <summary>Removes the user name of <paramref name="id"/>; X-Ray's own name shows again. Also discards a stale or orphaned name.</summary>
    public string? Clear(string id)
    {
        if (!_doc.Names.Remove(id)) return $"'{id}' has no saved name.";
        return Commit();
    }

    /// <summary>Keeps a stale name on its (changed) object: the fingerprint is updated to the object as it is now.</summary>
    public string? Confirm(string id)
    {
        if (Names.StatusOf(id) != NameStatus.Stale) return $"'{id}' has no name waiting for review.";
        _doc.Names[id] = Names.NewRecord(id, _doc.Names[id].Label, _clock());
        return Commit();
    }

    /// <summary>Moves a stale or orphaned name to another object (the user's choice; suggestions are only hints).</summary>
    public string? Reattach(string fromId, string toId)
    {
        if (!_doc.Names.TryGetValue(fromId, out var rec)) return $"'{fromId}' has no saved name.";
        if (Names.StatusOf(fromId) == NameStatus.Current) return $"The name of '{fromId}' is current; rename instead.";
        if (!Names.CanRename(toId)) return $"'{toId}' cannot carry a name.";
        if (_doc.Names.ContainsKey(toId) && toId != fromId) return $"'{toId}' already has a name; clear it first.";
        _doc.Names.Remove(fromId);
        _doc.Names[toId] = Names.NewRecord(toId, rec.Label, _clock());
        return Commit();
    }

    private string? Commit()
    {
        _store.Save(_doc);
        Apply();
        return null;
    }

    private void Apply()
    {
        Names = new SemanticNames(Index, _doc.Names);
        Index.UseNames(Names);
    }
}
