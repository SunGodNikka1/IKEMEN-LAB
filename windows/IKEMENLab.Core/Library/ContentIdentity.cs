using IKEMENLab.Core.Models;

namespace IKEMENLab.Core.Library;

public enum LibraryContentKind
{
    Character,
    Stage
}

/// <summary>
/// Stable identity for Date Added records inside one IKEMEN installation.
///
/// Characters are identified by their top-level folder under chars/ — the unit that is installed,
/// replaced or deleted (a nested "Pack/Ken/Ken.def" belongs to "pack"). Renaming the DEF inside, or
/// replacing the whole folder with a newer version, keeps the identity. Stages are identified by the
/// DEF path relative to stages/ ("pack/fancy.def"). Keys are lower-case with '/' separators, so case
/// and slash differences never split one item into two; display names are never used.
/// </summary>
public static class ContentIdentity
{
    public static string ForCharacter(CharacterEntry entry) => ForCharacterFolder(TopFolder(entry.Id));

    public static string ForCharacterFolder(string topFolderName)
        => "character:" + Normalize(topFolderName).Split('/')[0];

    public static string ForStage(StageEntry entry) => ForStageDef(entry.DefPath);

    /// <param name="pathRelativeToStages">e.g. "kfm.def" or "Pack/fancy.def".</param>
    public static string ForStageDef(string pathRelativeToStages)
    {
        var rel = Normalize(pathRelativeToStages);
        if (rel.StartsWith("stages/", StringComparison.Ordinal)) rel = rel["stages/".Length..];
        return "stage:" + rel;
    }

    /// <summary>File-system path whose timestamps are the arrival evidence for this identity.</summary>
    public static string AnchorPath(string root, string identity)
    {
        var (kind, key) = Split(identity);
        return kind == LibraryContentKind.Character
            ? Path.Combine(root, "chars", key)
            : Path.Combine(root, "stages", key.Replace('/', Path.DirectorySeparatorChar));
    }

    public static (LibraryContentKind Kind, string Key) Split(string identity)
    {
        if (identity.StartsWith("character:", StringComparison.Ordinal))
            return (LibraryContentKind.Character, identity["character:".Length..]);
        if (identity.StartsWith("stage:", StringComparison.Ordinal))
            return (LibraryContentKind.Stage, identity["stage:".Length..]);
        throw new ArgumentException("Unknown identity: " + identity, nameof(identity));
    }

    public static string TopFolder(string characterId)
        => characterId.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? characterId;

    private static string Normalize(string path)
        => path.Replace('\\', '/').Trim().Trim('/').ToLowerInvariant();
}
