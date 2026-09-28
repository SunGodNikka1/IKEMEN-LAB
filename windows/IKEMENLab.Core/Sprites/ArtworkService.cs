using IKEMENLab.Core.Models;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Validation;

namespace IKEMENLab.Core.Sprites;

public sealed record CharacterArtworkPaths(string DefPath, string Folder, string? SpriteFile, string? Pal1);

/// <summary>
/// Read-only artwork lookup for the browsers: resolves a character's SFF and pal1 ACT (IKEMEN lookup
/// order) and a stage's SFF, decodes the representative sprite and serves it through the disk cache.
/// Never writes beneath the IKEMEN root.
/// </summary>
public sealed class ArtworkService
{
    public const int PortraitMaxSize = 480;
    public const int StagePreviewMaxWidth = 640;
    public const int StagePreviewMaxHeight = 360;

    private readonly string _root;
    private readonly ThumbnailCache _cache;

    public ArtworkService(string root, ThumbnailCache cache)
    {
        _root = Path.GetFullPath(root);
        _cache = cache;
        _cache.EnsureOutside(_root);
    }

    public CharacterArtworkPaths ResolveCharacter(CharacterEntry character)
    {
        var def = Path.GetFullPath(Path.Combine(_root, character.DefPath));
        var folder = Path.GetDirectoryName(def)!;
        var parsed = DefParser.ParseFile(def);
        var sprite = parsed?.Value("sprite", "files");
        var pal1 = parsed?.Value("pal1", "files");
        return new CharacterArtworkPaths(
            def,
            folder,
            string.IsNullOrWhiteSpace(sprite) ? null : ContentValidator.ResolveResource(_root, def, sprite),
            string.IsNullOrWhiteSpace(pal1) ? null : ContentValidator.ResolveResource(_root, def, pal1));
    }

    public string? ResolveStageSprite(StageEntry stage)
    {
        var def = Path.GetFullPath(Path.Combine(_root, stage.RootRelativeDefPath));
        var spr = DefParser.ParseFile(def)?.Value("spr", "bgdef");
        return string.IsNullOrWhiteSpace(spr) ? null : ContentValidator.ResolveResource(_root, def, spr);
    }

    /// <summary>PNG bytes of the character portrait, or null when the character has none.</summary>
    public byte[]? CharacterPortraitPng(CharacterEntry character)
    {
        var paths = ResolveCharacter(character);
        var sources = new[] { paths.DefPath, paths.SpriteFile, paths.Pal1, Path.Combine(paths.Folder, "portrait.png") };
        return _cache.GetOrCreate("portrait", sources, () =>
        {
            var extracted = SpriteExtractor.ExtractCharacterPortrait(paths.Folder, paths.DefPath, paths.SpriteFile, paths.Pal1);
            return extracted is null ? null : SpriteImageOps.FitWithin(extracted.Image, PortraitMaxSize, PortraitMaxSize);
        });
    }

    /// <summary>PNG bytes of the stage preview, or null when the SFF is missing/undecodable.</summary>
    public byte[]? StagePreviewPng(StageEntry stage)
    {
        var sff = ResolveStageSprite(stage);
        if (sff is null) return null;
        var def = Path.Combine(_root, stage.RootRelativeDefPath);
        return _cache.GetOrCreate("stage", [def, sff], () =>
        {
            var extracted = SpriteExtractor.ExtractStagePreview(sff);
            return extracted is null
                ? null
                : SpriteImageOps.FitWithin(extracted.Image, StagePreviewMaxWidth, StagePreviewMaxHeight);
        });
    }
}
