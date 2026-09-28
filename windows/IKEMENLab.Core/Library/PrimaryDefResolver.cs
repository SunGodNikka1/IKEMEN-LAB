using System.Text.RegularExpressions;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Library;

/// <summary>Which rule decided a character package's primary DEF.</summary>
public enum PrimaryDefRule
{
    /// <summary>The folder has no valid character DEF.</summary>
    None,

    /// <summary>The DEF the user chose earlier (saved by IKEMEN Lab outside the IKEMEN root).</summary>
    SavedChoice,

    /// <summary>The only DEF of this folder that select.def uses.</summary>
    Roster,

    /// <summary>Only one usable candidate.</summary>
    OnlyCandidate,

    /// <summary>The DEF named after the character folder (IKEMEN's "Muzan" → Muzan/Muzan.def convention).</summary>
    FolderName,

    /// <summary>The base DEF of one character's AI / no-AI alternatives (all share one sprite file).</summary>
    AiVariantBase,

    /// <summary>Several plausible DEFs and nothing decides between them: the user must choose.</summary>
    Ambiguous
}

/// <summary>One character DEF inside a package or character folder, with the facts the rules use.</summary>
public sealed record DefCandidate
{
    public required string FullPath { get; init; }

    /// <summary>Relative to the package/character folder, '/' separators ("Muzan_AI.def", "Muzan/Muzan.def").</summary>
    public required string RelativePath { get; init; }

    public string FileName => Path.GetFileName(FullPath);
    public string Stem => Path.GetFileNameWithoutExtension(FullPath);
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Author { get; init; }
    public string? VersionDate { get; init; }

    /// <summary>Resolved full path of the sprite file, when it exists.</summary>
    public string? SpriteFile { get; init; }

    /// <summary>[Files] references (sprite, anim, cmd, cns, st) that do not exist.</summary>
    public IReadOnlyList<string> MissingFiles { get; init; } = [];

    public bool IsComplete => MissingFiles.Count == 0;

    /// <summary>Stock Kung Fu Man template; never primary while another candidate exists.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>"AI" or "No AI" when the file name marks an AI alternative ("Muzan_AI", "Muzan (noAI)").</summary>
    public string? AiTag { get; init; }

    /// <summary>The file stem without its AI tag ("Muzan" for "Muzan_noAI").</summary>
    public string? AiBase { get; init; }

    public string CharacterName => !string.IsNullOrWhiteSpace(Name) ? Name!
        : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : Stem;

    /// <summary>One line for pickers: "Muzan_AI.def — Demon King Muzan · AI".</summary>
    public string Label
    {
        get
        {
            var notes = new List<string>();
            if (AiTag is not null) notes.Add(AiTag);
            if (IsPlaceholder) notes.Add("KFM placeholder");
            if (!IsComplete) notes.Add("missing " + string.Join(", ", MissingFiles.Take(2)));
            var text = $"{RelativePath} — {CharacterName}";
            if (!string.IsNullOrWhiteSpace(Author)) text += " by " + Author;
            return notes.Count == 0 ? text : text + " · " + string.Join(" · ", notes);
        }
    }
}

public sealed record PrimaryDefDecision(
    IReadOnlyList<DefCandidate> Candidates, DefCandidate? Primary, PrimaryDefRule Rule, string FolderName = "")
{
    public bool IsAmbiguous => Rule == PrimaryDefRule.Ambiguous;

    /// <summary>
    /// The candidate that labels the package while no primary is decided (name, portrait). It is never
    /// written to select.def on its own: an ambiguous package still requires the user's choice.
    /// </summary>
    public DefCandidate? DisplayCandidate => Primary ?? PrimaryDefResolver.PreferenceOrder(Candidates, FolderName).FirstOrDefault();
}

/// <summary>Context that can settle a choice before the file-based rules run.</summary>
public sealed record PrimaryDefHints
{
    /// <summary>Saved user choice, relative to the folder.</summary>
    public string? SavedChoice { get; init; }

    /// <summary>DEFs of this folder that select.def lists uncommented, relative to the folder.</summary>
    public IReadOnlyList<string> ActiveRoster { get; init; } = [];

    /// <summary>DEFs of this folder that select.def lists commented out, relative to the folder.</summary>
    public IReadOnlyList<string> DisabledRoster { get; init; } = [];

    /// <summary>Further directories IKEMEN searches for referenced files (the root, data/).</summary>
    public IReadOnlyList<string> SearchRoots { get; init; } = [];
}

/// <summary>
/// The one primary-DEF decision shared by the installer, the library index and the fullgame importer.
/// A character folder's identity is its folder; which DEF inside it is "the character" is decided here,
/// deterministically, and never by file order. When nothing decides, the result is Ambiguous and the
/// caller must ask the user instead of guessing.
/// </summary>
public static partial class PrimaryDefResolver
{
    public static PrimaryDefDecision Resolve(string folder, string folderName, PrimaryDefHints? hints = null)
    {
        hints ??= new PrimaryDefHints();
        var candidates = FindCandidates(folder, hints);
        PrimaryDefDecision Decide(DefCandidate? primary, PrimaryDefRule rule) => new(candidates, primary, rule, folderName);
        if (candidates.Count == 0) return Decide(null, PrimaryDefRule.None);

        if (hints.SavedChoice is { } saved && Find(candidates, saved) is { } chosen)
            return Decide(chosen, PrimaryDefRule.SavedChoice);

        var active = hints.ActiveRoster.Select(p => Find(candidates, p)).OfType<DefCandidate>().Distinct().ToList();
        if (active.Count == 1)
            return Decide(active[0], PrimaryDefRule.Roster);
        if (active.Count == 0)
        {
            var disabled = hints.DisabledRoster.Select(p => Find(candidates, p)).OfType<DefCandidate>().Distinct().ToList();
            if (disabled.Count == 1)
                return Decide(disabled[0], PrimaryDefRule.Roster);
        }

        // Usable pool: complete, non-placeholder DEFs; relax only when nothing else remains.
        var pool = candidates.Where(c => c.IsComplete && !c.IsPlaceholder).ToList();
        if (pool.Count == 0) pool = candidates.Where(c => c.IsComplete).ToList();
        if (pool.Count == 0) pool = candidates.ToList();

        if (pool.Count == 1)
            return Decide(pool[0], PrimaryDefRule.OnlyCandidate);

        // The DEF IKEMEN's short form resolves to ("Muzan" → Muzan/Muzan.def) first, then a unique
        // same-named DEF one level down ("Muzan/Muzan/Muzan.def").
        var shortForm = Find(pool, folderName + ".def");
        if (shortForm is not null)
            return Decide(shortForm, PrimaryDefRule.FolderName);
        var named = pool.Where(c => string.Equals(c.Stem, folderName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 1)
            return Decide(named[0], PrimaryDefRule.FolderName);

        if (AiVariantBase(pool) is { } baseDef)
            return Decide(baseDef, PrimaryDefRule.AiVariantBase);

        return Decide(null, PrimaryDefRule.Ambiguous);
    }

    /// <summary>
    /// Valid character DEFs directly in <paramref name="folder"/>; when there are none, those one level
    /// down (the "Muzan/Muzan/Muzan.def" layout). DEFs that select.def uses anywhere below the folder are
    /// always included, so the roster's choice can be recognised.
    /// </summary>
    public static IReadOnlyList<DefCandidate> FindCandidates(string folder, PrimaryDefHints? hints = null)
    {
        hints ??= new PrimaryDefHints();
        var full = Path.GetFullPath(folder);
        var paths = CharacterDefsIn(full).ToList();
        if (paths.Count == 0)
        {
            foreach (var sub in SafeDirectories(full).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (IsSkippedFolder(sub)) continue;
                paths.AddRange(CharacterDefsIn(sub));
            }
        }

        foreach (var rel in hints.ActiveRoster.Concat(hints.DisabledRoster))
        {
            var candidate = Path.GetFullPath(Path.Combine(full, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnder(full, candidate) || paths.Contains(candidate, StringComparer.OrdinalIgnoreCase)) continue;
            if (File.Exists(candidate) && DefContentClassifier.IsValidCharacterDefFile(candidate)) paths.Add(candidate);
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => Describe(p, full, hints.SearchRoots))
            .OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Reads the facts the rules need from one DEF.</summary>
    public static DefCandidate Describe(string defPath, string folder, IReadOnlyList<string>? searchRoots = null)
    {
        var parsed = SafeParse(defPath);
        var defDir = Path.GetDirectoryName(defPath)!;
        var missing = new List<string>();
        string? sprite = null;
        foreach (var key in ReferenceKeys)
        {
            var value = parsed?.Value(key, "files")?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value)) continue;
            var resolved = ResolveReference(value, defDir, searchRoots ?? []);
            if (resolved is null) missing.Add(value);
            else if (key == "sprite") sprite = resolved;
        }

        var stem = Path.GetFileNameWithoutExtension(defPath);
        var (aiTag, aiBase) = ParseAiTag(stem);
        var spriteValue = parsed?.Value("sprite", "files");
        return new DefCandidate
        {
            FullPath = defPath,
            RelativePath = Path.GetRelativePath(folder, defPath).Replace('\\', '/'),
            Name = Clean(parsed?.Name),
            DisplayName = Clean(parsed?.DisplayName),
            Author = Clean(parsed?.Author),
            VersionDate = Clean(parsed?.VersionDate),
            SpriteFile = sprite,
            MissingFiles = missing,
            IsPlaceholder = IsKfmPlaceholder(parsed, spriteValue),
            AiTag = aiTag,
            AiBase = aiBase
        };
    }

    /// <summary>Deterministic display order: usable and conventional first, then by path.</summary>
    public static IEnumerable<DefCandidate> PreferenceOrder(IEnumerable<DefCandidate> candidates, string? folderName)
        => candidates
            .OrderBy(c => c.IsPlaceholder)
            .ThenBy(c => !c.IsComplete)
            .ThenBy(c => folderName is null || !string.Equals(c.Stem, folderName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.AiTag is not null)
            .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "Muzan_AI" → ("AI", "Muzan"); "Muzan_noAI" / "Muzan (No AI)" / "MuzanNoAI" → ("No AI", "Muzan").
    /// A tag needs a separator or a camel-case boundary, so names like "Kai" or "Sai" are not tags.
    /// </summary>
    public static (string? Tag, string? Base) ParseAiTag(string stem)
    {
        var match = SeparatedAiTag().Match(stem);
        if (!match.Success) match = CamelAiTag().Match(stem);
        if (!match.Success) return (null, null);
        var tag = match.Groups["tag"].Value.ToLowerInvariant();
        var baseName = match.Groups["base"].Value.Trim(' ', '_', '-', '.');
        if (baseName.Length == 0) return (null, null);
        var negative = tag.StartsWith("no", StringComparison.Ordinal) ||
                       (tag.StartsWith('w') && !tag.StartsWith("with", StringComparison.Ordinal));
        return (negative ? "No AI" : "AI", baseName);
    }

    [GeneratedRegex(@"^(?<base>.+?)(?:[ _\-.]+\(?|\()(?<tag>no[ _\-]?ai|with[ _\-]?ai|w[ _\-/]?o[ _\-]?ai|ai|cpu)\)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedAiTag();

    [GeneratedRegex(@"^(?<base>.*?[a-z0-9])(?<tag>NoAI|NOAI|AI)$", RegexOptions.CultureInvariant)]
    private static partial Regex CamelAiTag();

    private static readonly string[] ReferenceKeys = ["sprite", "anim", "cmd", "cns", "st"];

    /// <summary>
    /// One base DEF plus AI-tagged alternatives of that same name, all using one sprite file:
    /// Muzan.def + Muzan_AI.def + Muzan_noAI.def → Muzan.def.
    /// </summary>
    private static DefCandidate? AiVariantBase(IReadOnlyList<DefCandidate> pool)
    {
        if (pool.Count < 2) return null;
        var sprite = pool[0].SpriteFile;
        if (sprite is null || pool.Any(c => !string.Equals(c.SpriteFile, sprite, StringComparison.OrdinalIgnoreCase)))
            return null;

        var bases = pool.Where(c => c.AiTag is null).ToList();
        if (bases.Count != 1) return null;
        var baseDef = bases[0];
        return pool.All(c => c == baseDef || string.Equals(c.AiBase, baseDef.Stem, StringComparison.OrdinalIgnoreCase))
            ? baseDef
            : null;
    }

    private static bool IsKfmPlaceholder(DefParseResult? parsed, string? spriteValue)
    {
        var spriteName = string.IsNullOrWhiteSpace(spriteValue) ? null : Path.GetFileName(spriteValue.Trim().Trim('"').Replace('\\', '/'));
        if (spriteName is not null &&
            (spriteName.Equals("kfm.sff", StringComparison.OrdinalIgnoreCase) ||
             spriteName.Equals("kfm720.sff", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var name = (parsed?.Name ?? parsed?.DisplayName ?? string.Empty).Trim().Trim('"');
        var author = parsed?.Author ?? string.Empty;
        return (name.Equals("Kung Fu Man", StringComparison.OrdinalIgnoreCase) || name.Equals("kfm", StringComparison.OrdinalIgnoreCase)) &&
               author.Contains("Elecbyte", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveReference(string value, string defDir, IReadOnlyList<string> searchRoots)
    {
        var relative = value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        try
        {
            foreach (var baseDir in new[] { defDir }.Concat(searchRoots))
            {
                var candidate = Path.GetFullPath(Path.Combine(baseDir, relative));
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return null;
    }

    private static DefCandidate? Find(IReadOnlyList<DefCandidate> candidates, string relative)
    {
        var norm = relative.Replace('\\', '/').Trim().TrimStart('/');
        return candidates.FirstOrDefault(c => string.Equals(c.RelativePath, norm, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> CharacterDefsIn(string dir)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.def").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (Path.GetFileName(file).StartsWith('.')) continue;
            if (DefContentClassifier.IsValidCharacterDefFile(file)) yield return Path.GetFullPath(file);
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsSkippedFolder(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.StartsWith('.') || name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase) || IkemenLabStaging.IsStagingName(dir);
    }

    private static bool IsUnder(string parent, string child)
        => child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static DefParseResult? SafeParse(string path)
    {
        try
        {
            return DefParser.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
