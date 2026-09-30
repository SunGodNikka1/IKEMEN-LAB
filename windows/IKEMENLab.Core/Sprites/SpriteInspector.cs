using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Sprites;

public enum FindingSeverity { Info, Warning, Error }

public sealed record InspectorFinding(FindingSeverity Severity, string Message);

/// <summary>One sprite table entry as stored in the SFF (linked sprites report the size of the sprite they reuse).</summary>
public sealed record SpriteInfo(
    int Index, int Group, int Number, int Width, int Height, int AxisX, int AxisY,
    string Encoding, long StoredBytes, int? LinkedTo, int Palette, bool? Decodes)
{
    public string Id => $"{Group},{Number}";
}

public sealed record GroupSummary(int Group, int Count);

public sealed class SpriteReport
{
    public required string Path { get; init; }
    public required int Version { get; init; }
    public required long FileBytes { get; init; }
    public required int PaletteCount { get; init; }
    public required IReadOnlyList<SpriteInfo> Sprites { get; init; }
    public required IReadOnlyList<GroupSummary> Groups { get; init; }
    public required IReadOnlyList<InspectorFinding> Findings { get; init; }

    public string VersionText => Version == 2 ? "SFF v2" : "SFF v1";
}

/// <summary>A palette a sprite can be previewed with: the character's ACT files and embedded SFF palettes.</summary>
public sealed record PreviewPalette(string Label, uint[] Colors);

/// <summary>
/// Read-only look inside an SFF: the sprite table, encodings, axes, palettes, structural problems, and a
/// renderer for a single sprite. Nothing here writes to the IKEMEN folder.
/// </summary>
public static class SpriteInspector
{
    public const int LargeSpriteEdge = 2048;

    /// <param name="verifyDecode">Decode every sprite to find the ones that cannot be drawn (slower on big SFFs).</param>
    /// <param name="isCharacter">Adds checks for the sprites a character needs (0,0 standing frame, 9000,0 select portrait).</param>
    public static SpriteReport? Inspect(SffFile sff, string path, bool verifyDecode = true, bool isCharacter = false,
        CancellationToken cancel = default)
    {
        var infos = new List<SpriteInfo>(sff.Sprites.Count);
        foreach (var sprite in sff.Sprites)
        {
            cancel.ThrowIfCancellationRequested();
            var (w, h) = sff.Dimensions(sprite);
            bool? decodes = verifyDecode ? sff.Decode(sprite) is not null : null;
            infos.Add(new SpriteInfo(
                sprite.Index, sprite.Group, sprite.Number, w, h, sprite.AxisX, sprite.AxisY,
                EncodingName(sff, sprite), sff.StoredSize(sprite),
                sprite.DataLength == 0 && sprite.Link != sprite.Index && sprite.Link < sff.Sprites.Count ? sprite.Link : null,
                sff.Version == 2 ? sprite.PaletteIndex : -1,
                decodes));
        }

        long fileBytes = 0;
        try { fileBytes = new FileInfo(path).Length; } catch (IOException) { }

        var groups = infos.GroupBy(i => i.Group).OrderBy(g => g.Key).Select(g => new GroupSummary(g.Key, g.Count())).ToList();
        return new SpriteReport
        {
            Path = path,
            Version = sff.Version,
            FileBytes = fileBytes,
            PaletteCount = sff.PaletteCount,
            Sprites = infos,
            Groups = groups,
            Findings = Findings(sff, infos, isCharacter)
        };
    }

    public static SpriteReport? Inspect(string sffPath, bool verifyDecode = true, bool isCharacter = false,
        CancellationToken cancel = default)
    {
        using var sff = SffFile.Open(sffPath);
        return sff is null ? null : Inspect(sff, sffPath, verifyDecode, isCharacter, cancel);
    }

    private static string EncodingName(SffFile sff, SffSprite sprite)
    {
        if (sff.Version == 1) return "PCX";
        return sprite.Format switch
        {
            0 => sprite.ColorDepth == 8 ? "Raw 8-bit" : $"Raw {sprite.ColorDepth}-bit",
            2 => "RLE8",
            3 => "RLE5",
            4 => "LZ5",
            10 => "PNG 8-bit",
            11 => "PNG 24-bit",
            12 => "PNG 32-bit",
            var f => $"Unknown ({f})"
        };
    }

    private static List<InspectorFinding> Findings(SffFile sff, List<SpriteInfo> sprites, bool isCharacter)
    {
        var findings = new List<InspectorFinding>();
        if (sprites.Count == 0)
        {
            findings.Add(new(FindingSeverity.Error, "The SFF contains no sprites."));
            return findings;
        }

        var duplicates = sprites.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            findings.Add(new(FindingSeverity.Warning,
                $"{duplicates.Count} sprite number(s) appear more than once ({Preview(duplicates)}); the engine keeps only one of each."));

        var failed = sprites.Where(s => s.Decodes == false).Select(s => s.Id).ToList();
        if (failed.Count > 0)
            findings.Add(new(FindingSeverity.Error, $"{failed.Count} sprite(s) cannot be decoded ({Preview(failed)})."));

        var broken = sprites.Where(s => sff.Sprites[s.Index].DataLength == 0 && s.LinkedTo is null).Select(s => s.Id).ToList();
        if (broken.Count > 0)
            findings.Add(new(FindingSeverity.Error, $"{broken.Count} linked sprite(s) point at a sprite that does not exist ({Preview(broken)})."));

        var large = sprites.Where(s => s.Width > LargeSpriteEdge || s.Height > LargeSpriteEdge).Select(s => s.Id).ToList();
        if (large.Count > 0)
            findings.Add(new(FindingSeverity.Warning,
                $"{large.Count} sprite(s) are larger than {LargeSpriteEdge}px on a side ({Preview(large)}); they use a lot of video memory."));

        if (isCharacter)
        {
            if (!sprites.Any(s => s.Group == 0 && s.Number == 0))
                findings.Add(new(FindingSeverity.Info, "No sprite 0,0 (standing frame): the character has no idle sprite to fall back on."));
            if (!sprites.Any(s => s.Group == 9000 && s.Number == 0))
                findings.Add(new(FindingSeverity.Info, "No sprite 9000,0: character select shows no portrait for this character."));
        }

        if (findings.Count == 0)
            findings.Add(new(FindingSeverity.Info, "No problems found."));
        return findings;
    }

    private static string Preview(List<string> ids) =>
        string.Join(", ", ids.Take(6)) + (ids.Count > 6 ? $", +{ids.Count - 6} more" : string.Empty);

    /// <summary>Renders one sprite as a PNG (straight alpha). Null when it cannot be decoded.</summary>
    public static byte[]? RenderPng(SffFile sff, SffSprite sprite, uint[]? palette = null)
    {
        var image = sff.Decode(sprite, palette);
        return image is null ? null : Png.Encode(image);
    }

    /// <summary>
    /// The palettes a character's sprites can be previewed with, in the order IKEMEN numbers them.
    /// </summary>
    public static IReadOnlyList<PreviewPalette> PalettesFor(string ikemenRoot, CharacterEntry character)
    {
        var def = System.IO.Path.GetFullPath(System.IO.Path.Combine(ikemenRoot, character.DefPath));
        var text = DefFileReader.ReadFileContent(def);
        var parsed = text is null ? null : DefParser.Parse(text);
        var sffPath = CharacterDetailsReader.ResolveSprite(ikemenRoot, character);
        return CharacterDetailsReader.CollectPaletteSets(ikemenRoot, def, parsed, sffPath)
            .Select(p => new PreviewPalette($"Palette {p.Number} — {p.Source}", p.Palette))
            .ToList();
    }
}
