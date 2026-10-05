using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Director;

/// <summary>
/// What the compiler and the ownership check need to know about the character's own files, read the way IKEMEN loads them: the DEF's localcoord and
/// [Files], the state files in engine order (<c>st</c>, then <c>st&lt;n&gt;</c> in natural order, then the CMD), which of them declares the
/// <c>[Statedef -1]</c> the engine uses, a block of unused state numbers for the generated states, and the animations the chase plays.
/// </summary>
/// <param name="Folder">The character folder these facts were read from (installed, base or working copy).</param>
/// <param name="DefFile">The DEF's file name inside <paramref name="Folder"/>.</param>
/// <param name="MinusOneFile">The file (inside the folder) holding the State -1 the engine uses; null when none or more than one declares it.</param>
/// <param name="MinusOneDeclaredIn">Every loaded file that declares <c>[Statedef -1]</c> (more than one is refused: which wins depends on the engine's merge rules).</param>
public sealed record CharacterFacts(
    string Folder, string DefFile, int LocalCoord, bool IkemenVersion, IReadOnlyList<string> StateFiles, string? CmdFile,
    string? MinusOneFile, IReadOnlyList<string> MinusOneDeclaredIn, int StateBase, string StKey,
    bool HasWalkAnim, bool HasWalkBackAnim, bool HasStandAnim)
{
    /// <summary>The generated file (in the character folder) holding the Director's states.</summary>
    public const string GeneratedFile = "ikemenlab_director.cns";
    public int ChaseState => StateBase;
    public int RetreatState => StateBase + 1;
    /// <summary>The fighter's units per world (320-wide) unit: a distance of N px is N × this in P2Dist X.</summary>
    public double UnitsPerPx => LocalCoord / 320.0;
}

public static partial class DirectorFacts
{
    /// <summary>Candidate first numbers for the generated states; the first whose block of 10 is unused in the character wins (deterministic).</summary>
    private static readonly int[] StateBases = [9790, 8790, 7790, 19790, 29790, 39790];

    public static CharacterFacts Read(string folder, string defFile, SemanticIndex index)
    {
        var def = Path.Combine(folder, defFile);
        var parsed = Parsing.DefParser.ParseFile(def) ?? throw new FileNotFoundException("The character's DEF could not be read.", def);
        var local = BehaviorCoordinatesWidth(parsed.Value("localcoord", "info"));
        var ikemen = !string.IsNullOrWhiteSpace(parsed.Value("ikemenversion", "info"));

        // [Files] st / st<n> in the engine's order: "st" first, then st<n> naturally sorted; the CMD is compiled after them.
        var files = parsed.SectionValues.TryGetValue("files", out var f) ? f : new Dictionary<string, string>();
        var stKeys = files.Keys.Where(k => StKey().IsMatch(k)).OrderBy(k => k == "st" ? -1 : int.Parse(k[2..], CultureInfo.InvariantCulture)).ToList();
        var stateFiles = stKeys.Select(k => Clean(files[k])).Where(v => v.Length > 0).ToList();
        var cmd = files.TryGetValue("cmd", out var c) ? Clean(c) : null;
        var declared = new List<string>();
        foreach (var file in stateFiles.Append(cmd).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, file);
            if (File.Exists(path) && Lines(ReadText(path)).Any(l => IsStatedef(l, -1))) declared.Add(file);
        }

        var used = Enumerable.Range(1, 99).Select(n => "st" + n.ToString(CultureInfo.InvariantCulture)).First(k => !files.ContainsKey(k));
        var stateBase = StateBases.First(b => Enumerable.Range(b, 10).All(n => index.Get("state:" + n.ToString(CultureInfo.InvariantCulture)) is null));
        return new CharacterFacts(folder, defFile, local, ikemen, stateFiles, cmd, declared.Count == 1 ? declared[0] : null, declared, stateBase, used,
            index.Get("anim:20") is not null, index.Get("anim:21") is not null, index.Get("anim:0") is not null);
    }

    private static int BehaviorCoordinatesWidth(string? value)
    {
        var first = value?.Split(',')[0].Trim();
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w > 0 ? w : 320;
    }

    /// <summary>A DEF value without its trailing comment or quotes.</summary>
    private static string Clean(string value) => value.Split(';')[0].Trim().Trim('"').Replace('\\', '/');

    /// <summary>Character files are read and written byte-for-byte (Latin-1 maps every byte to one char), so Shift-JIS or ANSI text survives untouched.</summary>
    public static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>
    /// A character file as one char per byte. <c>File.ReadAllText(path, Latin1)</c> is not that: it still detects a byte-order mark, switches to UTF-8
    /// and drops the mark (KFM's CMD starts with one), so the bytes written back would not be the file's own.
    /// </summary>
    public static string ReadText(string path) => Latin1.GetString(File.ReadAllBytes(path));

    /// <summary>Writes text read by <see cref="ReadText"/> back byte for byte (Latin-1 has no preamble of its own).</summary>
    public static void WriteText(string path, string text) => File.WriteAllBytes(path, Latin1.GetBytes(text));

    public static IReadOnlyList<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Whether <paramref name="line"/> is the header <c>[Statedef n]</c> (any case and spacing, comments allowed after it).</summary>
    public static bool IsStatedef(string line, int n) =>
        StatedefHeader().Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v == n;

    public static bool IsAnyStatedef(string line) => StatedefHeader().IsMatch(line);

    // A UTF-8 byte-order mark read as Latin-1 ("ï»¿") may precede a header on a file's first line.
    [GeneratedRegex(@"^(?:ï»¿)?\s*\[\s*statedef\s+(-?\d+)\s*(?:,[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex StatedefHeader();

    [GeneratedRegex(@"^st\d*$")]
    private static partial Regex StKey();
}
