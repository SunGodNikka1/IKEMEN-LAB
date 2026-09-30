using System.Globalization;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Sprites;
using IKEMENLab.Core.Validation;

namespace IKEMENLab.Core.Characters;

/// <summary>A [Data] value from the character's CNS, or the engine default when the key is absent.</summary>
public sealed record CnsValue(int Value, bool IsEngineDefault);

public sealed record CharacterStats(CnsValue Life, CnsValue Attack, CnsValue Defence, CnsValue Power, string CnsPath)
{
    // Scale maxima used by the macOS bars.
    public const int MaxLife = 2000;
    public const int MaxAttack = 200;
    public const int MaxDefence = 200;
    public const int MaxPower = 5000;
}

public sealed record PaletteSlot(int Number, string Source, uint? SwatchArgb);

public sealed record CharacterFeatures(bool HasIntro, bool HasSound, bool HasAi)
{
    public IEnumerable<string> Labels()
    {
        if (HasIntro) yield return "INTRO";
        if (HasSound) yield return "SFX";
        if (HasAi) yield return "AI";
    }
}

public sealed record CharacterDetails
{
    public required string DefPath { get; init; }
    public required string EngineLabel { get; init; }
    public required string VersionDate { get; init; }

    /// <summary>Null when the CNS is missing or unreadable (never replaced by invented numbers).</summary>
    public CharacterStats? Stats { get; init; }

    public required IReadOnlyList<PaletteSlot> Palettes { get; init; }
    public required IReadOnlyList<CharacterMove> Moves { get; init; }
    public required string? DefText { get; init; }
}

/// <summary>
/// Read-only inspector data for one character: CNS [Data] stats, selectable palettes with real swatch
/// colours, move list, engine label and the DEF text. Files are resolved in IKEMEN's lookup order.
/// </summary>
public static class CharacterDetailsReader
{
    public const int MaxPalettes = 12;
    private const int MaxDefTextBytes = 256 * 1024;

    public static CharacterDetails Read(string root, CharacterEntry character)
    {
        var def = Path.GetFullPath(Path.Combine(root, character.DefPath));
        var defText = ReadDefText(def);
        var parsed = defText is null ? null : DefParser.Parse(defText);

        var cns = Resolve(root, def, parsed?.Value("cns", "files"));
        var cmd = Resolve(root, def, parsed?.Value("cmd", "files"));
        var sff = Resolve(root, def, parsed?.Value("sprite", "files"));

        return new CharacterDetails
        {
            DefPath = character.DefPath,
            EngineLabel = EngineLabel(parsed),
            VersionDate = VersionDateFormatter.Format(parsed?.VersionDate ?? character.VersionDate),
            Stats = cns is null ? null : ReadStats(cns),
            Palettes = ReadPalettes(root, def, parsed, sff),
            Moves = cmd is null ? Array.Empty<CharacterMove>() : CmdMoveReader.ReadFile(cmd),
            DefText = defText
        };
    }

    /// <summary>
    /// Any file the DEF's [Files] section names (cmd, cns, st, st0..st9, stcommon, anim, sprite…), resolved in
    /// IKEMEN's lookup order. Null when the key is absent or the file cannot be found.
    /// </summary>
    public static string? ResolveFile(string root, CharacterEntry character, string key)
    {
        var def = Path.GetFullPath(Path.Combine(root, character.DefPath));
        var text = ReadDefText(def);
        var parsed = text is null ? null : DefParser.Parse(text);
        return Resolve(root, def, parsed?.Value(key, "files"));
    }

    /// <summary>The CNS the DEF's [Files] cns= names, resolved in IKEMEN's lookup order (null when absent).</summary>
    public static string? ResolveCns(string root, CharacterEntry character)
    {
        var def = Path.GetFullPath(Path.Combine(root, character.DefPath));
        var text = ReadDefText(def);
        var parsed = text is null ? null : DefParser.Parse(text);
        return Resolve(root, def, parsed?.Value("cns", "files"));
    }

    /// <summary>"MUGEN 1.1", "MUGEN 1.0", "IKEMEN" or "WinMUGEN" (no mugenversion key).</summary>
    public static string EngineLabel(DefParseResult? parsed)
    {
        if (parsed is null) return "Unknown";
        if (!string.IsNullOrWhiteSpace(parsed.Value("ikemenversion", "info"))) return "IKEMEN";
        var mugen = parsed.Value("mugenversion", "info") ?? parsed.Value("mugenversion");
        if (string.IsNullOrWhiteSpace(mugen)) return "WinMUGEN";
        if (mugen.Contains("1.1", StringComparison.Ordinal)) return "MUGEN 1.1";
        if (mugen.Contains("1.0", StringComparison.Ordinal) || mugen.StartsWith("1,", StringComparison.Ordinal)) return "MUGEN 1.0";
        // WinMUGEN-era characters store a build date here (e.g. "04,14,2001").
        if (mugen.Count(c => c is ',' or '.' or '/') >= 2) return "WinMUGEN";
        return "MUGEN";
    }

    public static CharacterStats? ReadStats(string cnsPath)
    {
        var content = DefFileReader.ReadFileContent(cnsPath);
        if (content is null) return null;

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inData = false;
        foreach (var raw in content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                if (inData) break; // [Data] is a single block; stop before the state machine
                var end = line.IndexOf(']');
                inData = end > 1 && line[1..end].Trim().Equals("Data", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inData) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..];
            var comment = value.IndexOf(';');
            if (comment >= 0) value = value[..comment];
            data[line[..eq].Trim()] = value.Trim();
        }

        CnsValue Get(string key, int engineDefault)
            => data.TryGetValue(key, out var v) &&
               double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? new CnsValue((int)Math.Round(d), false)
                : new CnsValue(engineDefault, true);

        return new CharacterStats(Get("life", 1000), Get("attack", 100), Get("defence", 100), Get("power", 3000), cnsPath);
    }

    /// <summary>
    /// Selectable palettes the way IKEMEN builds them: pal1..pal12 ACT files; for SFF v2 also the
    /// embedded 1,n palettes. Each swatch is the most prominent colour the standing sprite uses.
    /// </summary>
    public static IReadOnlyList<PaletteSlot> ReadPalettes(string root, string def, DefParseResult? parsed, string? sffPath)
    {
        using var sff = sffPath is null ? null : SffFile.Open(sffPath);
        var slots = Collect(root, def, parsed, sff);
        var usage = SpriteIndexUsage(sff);
        return slots.Select(s => new PaletteSlot(s.Number, s.Source, s.Palette is null ? null : Swatch(s.Palette, usage))).ToList();
    }

    /// <summary>The same palettes as <see cref="ReadPalettes"/>, with their colours, for previewing sprites.</summary>
    public static IReadOnlyList<(int Number, string Source, uint[] Palette)> CollectPaletteSets(
        string root, string def, DefParseResult? parsed, string? sffPath)
    {
        using var sff = sffPath is null ? null : SffFile.Open(sffPath);
        return Collect(root, def, parsed, sff)
            .Where(s => s.Palette is not null)
            .Select(s => (s.Number, s.Source, s.Palette!))
            .ToList();
    }

    /// <summary>The SFF the DEF's [Files] sprite= names, resolved in IKEMEN's lookup order (null when absent).</summary>
    public static string? ResolveSprite(string root, CharacterEntry character)
    {
        var def = Path.GetFullPath(Path.Combine(root, character.DefPath));
        var text = ReadDefText(def);
        var parsed = text is null ? null : DefParser.Parse(text);
        return Resolve(root, def, parsed?.Value("sprite", "files"));
    }

    private static List<(int Number, string Source, uint[]? Palette)> Collect(
        string root, string def, DefParseResult? parsed, SffFile? sff)
    {
        var slots = new List<(int Number, string Source, uint[]? Palette)>();

        for (var n = 1; n <= MaxPalettes; n++)
        {
            var act = Resolve(root, def, parsed?.Value("pal" + n, "files"));
            var actPalette = act is null ? null : ActPalette.Read(act);
            if (actPalette is not null)
            {
                slots.Add((n, Path.GetFileName(act!), actPalette));
                continue;
            }

            var embedded = sff?.PaletteByGroup(1, n);
            if (embedded is not null) slots.Add((n, "SFF palette 1," + n, embedded));
        }

        // SFF v1 without any ACT: the sprite's own palette is the only one.
        if (slots.Count == 0 && sff is { Version: 1 } && sff.Find(0, 0) is { } first)
        {
            slots.Add((1, "Embedded SFF palette", sff.EmbeddedPaletteFor(first)));
        }

        return slots;
    }

    /// <summary>Histogram of palette indices used by the standing sprite (0,0), if decodable.</summary>
    private static long[]? SpriteIndexUsage(SffFile? sff)
    {
        var sprite = sff?.Find(0, 0) ?? sff?.Find(9000, 1) ?? sff?.Find(9000, 0);
        if (sff is null || sprite is null) return null;
        var decoded = sff.DecodeIndices(sprite);
        if (decoded is null) return null;
        var hist = new long[256];
        foreach (var i in decoded.Value.Indices) hist[i]++;
        hist[0] = 0;
        return hist;
    }

    /// <summary>
    /// Most-used vivid colour (saturation/lightness in a useful range), else the most-used colour.
    /// Without usage data, the most vivid colour of the palette.
    /// </summary>
    public static uint? Swatch(uint[] palette, long[]? usage)
    {
        uint? best = null, fallback = null;
        double bestScore = -1, fallbackScore = -1;
        for (var i = 1; i < Math.Min(256, palette.Length); i++)
        {
            var argb = palette[i] | 0xFF000000u;
            var weight = usage is null ? 1 : usage[i];
            if (weight == 0) continue;
            var (s, l) = SatLight(argb);
            var vivid = s > 0.25 && l is > 0.12 and < 0.88;
            var score = usage is null ? s * (1 - Math.Abs(l - 0.5)) : weight;
            if (vivid && score > bestScore) { bestScore = score; best = argb; }
            if (score > fallbackScore) { fallbackScore = score; fallback = argb; }
        }

        return best ?? fallback;
    }

    private static (double S, double L) SatLight(uint argb)
    {
        double r = (argb >> 16 & 0xFF) / 255.0, g = (argb >> 8 & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var s = max == min ? 0 : l > 0.5 ? (max - min) / (2 - max - min) : (max - min) / (max + min);
        return (s, l);
    }

    private static string? Resolve(string root, string def, string? reference)
        => string.IsNullOrWhiteSpace(reference) ? null : ContentValidator.ResolveResource(root, def, reference);

    private static string? ReadDefText(string def)
    {
        try
        {
            var info = new FileInfo(def);
            if (!info.Exists) return null;
            var text = DefFileReader.ReadFileContent(def);
            if (text is not null && text.Length > MaxDefTextBytes) text = text[..MaxDefTextBytes] + "\n; … (truncated)";
            return text;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
