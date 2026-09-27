using IKEMENLab.Core.Models;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.SelectDef;

namespace IKEMENLab.Core.Validation;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ValidationIssue(ValidationSeverity Severity, string Message, string File, string? Suggestion = null);

public sealed record ValidationResult(string ContentName, string ContentType, string ContentPath, IReadOnlyList<ValidationIssue> Issues)
{
    public int ErrorCount => Issues.Count(i => i.Severity == ValidationSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == ValidationSeverity.Warning);
}

/// <summary>
/// Read-only port of the macOS ContentValidator. Detection only: nothing is renamed or rewritten.
/// Resource lookup follows IKEMEN's search order (DEF folder, then root, then data/) instead of the
/// Mac heuristic, so shared files such as data/common1.cns are not reported as missing.
/// </summary>
public static class ContentValidator
{
    private static readonly string[] RequiredCharacterFiles = ["sprite", "anim", "cmd", "cns"];
    private static readonly string[] OptionalCharacterFiles = ["sound", "ai"];
    // Characters a select.def roster line cannot carry (',' splits parameters, ';' starts a comment).
    private static readonly char[] RosterBreakingChars = [',', ';', '"'];

    /// <summary>Results that carry at least one error or warning (info-only results are omitted).</summary>
    public static IReadOnlyList<ValidationResult> ValidateLibrary(LibrarySnapshot snapshot, CancellationToken ct = default)
    {
        var root = snapshot.Installation.RootPath;
        var results = new List<ValidationResult>();

        foreach (var character in snapshot.Characters)
        {
            ct.ThrowIfCancellationRequested();
            var result = ValidateCharacter(root, character);
            if (result.ErrorCount + result.WarningCount > 0) results.Add(result);
        }

        foreach (var stage in snapshot.Stages)
        {
            ct.ThrowIfCancellationRequested();
            var result = ValidateStage(root, stage);
            if (result.ErrorCount + result.WarningCount > 0) results.Add(result);
        }

        if (snapshot.SelectDef is { IsAvailable: true } selectDef)
        {
            var roster = ValidateSelectDef(root, selectDef);
            if (roster.Issues.Count > 0) results.Add(roster);
        }

        return results;
    }

    public static ValidationResult ValidateCharacter(string root, CharacterEntry character)
    {
        var issues = new List<ValidationIssue>();
        var defFull = Path.Combine(root, character.DefPath);
        var defName = Path.GetFileName(character.DefPath);
        var parsed = DefParser.ParseFile(defFull);

        if (parsed is null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Cannot read .def file", defName,
                "Check file encoding (UTF-8, Windows-1252 or Shift-JIS)"));
            return new ValidationResult(character.DisplayName, "character", character.DefPath, issues);
        }

        foreach (var key in RequiredCharacterFiles)
        {
            var reference = parsed.Value(key, "files");
            if (string.IsNullOrWhiteSpace(reference))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    $"Missing required '{key}' reference in [Files] section", defName,
                    $"Add '{key} = <filename>' to [Files] section"));
                continue;
            }

            CheckResource(root, defFull, reference, Capitalize(key) + " file", ValidationSeverity.Error, issues);
        }

        foreach (var key in OptionalCharacterFiles)
        {
            var reference = parsed.Value(key, "files");
            if (!string.IsNullOrWhiteSpace(reference))
            {
                CheckResource(root, defFull, reference, Capitalize(key) + " file", ValidationSeverity.Warning, issues);
            }
        }

        issues.AddRange(ValidateName(Path.GetFileName(character.FolderPath), leadingBracketBreaks: true));
        return new ValidationResult(character.DisplayName, "character", character.DefPath, issues);
    }

    public static ValidationResult ValidateStage(string root, StageEntry stage)
    {
        var issues = new List<ValidationIssue>();
        var relative = stage.RootRelativeDefPath;
        var defFull = Path.Combine(root, relative);
        var defName = Path.GetFileName(relative);
        var stem = Path.GetFileNameWithoutExtension(relative);
        var parsed = DefParser.ParseFile(defFull);

        if (parsed is null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Cannot read .def file", defName,
                "Check file encoding (UTF-8, Windows-1252 or Shift-JIS)"));
            return new ValidationResult(stem, "stage", relative, issues);
        }

        var spr = parsed.Value("spr", "bgdef");
        if (string.IsNullOrWhiteSpace(spr))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "No sprite file (spr) defined", defName,
                "Add 'spr = <filename>.sff' to [BGdef] section"));
        }
        else
        {
            CheckResource(root, defFull, spr, "Sprite file (.sff)", ValidationSeverity.Error, issues);
        }

        issues.AddRange(ValidateName(stem, leadingBracketBreaks: false));
        return new ValidationResult(stem, "stage", relative, issues);
    }

    /// <summary>Roster lines pointing at content that does not exist (the Mac "missing" status).</summary>
    public static ValidationResult ValidateSelectDef(string root, SelectDefIndex index)
    {
        var issues = new List<ValidationIssue>();
        var file = Path.GetFileName(index.Location.Path ?? "select.def");

        foreach (var entry in index.MissingEntries)
        {
            var what = entry.Section == SelectDefSection.Characters ? "Character" : "Stage";
            issues.Add(new ValidationIssue(ValidationSeverity.Warning,
                $"{what} '{entry.RawName}' (line {entry.LineNumber}) not found",
                file,
                $"Expected {string.Join(" or ", entry.CandidateDefPaths)}"));
        }

        foreach (var entry in index.InvalidEntries)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning,
                $"Entry '{entry.RawName}' (line {entry.LineNumber}) is not a valid roster reference",
                file,
                "A .def entry needs its folder, e.g. 'kfm/kfm.def'"));
        }

        var display = index.Location.Path is null ? "select.def" : Path.GetRelativePath(root, index.Location.Path).Replace('\\', '/');
        return new ValidationResult(display, "select.def", display, issues);
    }

    private static void CheckResource(
        string root, string defFull, string reference, string resourceType,
        ValidationSeverity missingSeverity, List<ValidationIssue> issues)
    {
        var defName = Path.GetFileName(defFull);
        if (ResolveResource(root, defFull, reference) is not null) return;

        var near = FindSimilarName(root, defFull, reference);
        if (near is not null)
        {
            issues.Add(new ValidationIssue(missingSeverity,
                $"{resourceType} has special character mismatch: '{reference}' → actual: '{near}'",
                defName, $"The .def should reference '{near}'"));
            return;
        }

        var leaf = SelectDefReader.NormalizeSeparators(reference).Split('/').Last();
        issues.Add(new ValidationIssue(missingSeverity, $"{resourceType} not found: '{reference}'", defName,
            $"Check that '{leaf}' exists in the correct location"));
    }

    /// <summary>IKEMEN search order for files referenced by a DEF: DEF folder, root, data/.</summary>
    public static string? ResolveResource(string root, string defFull, string reference)
    {
        var normalized = SelectDefReader.NormalizeSeparators(reference.Trim());
        if (normalized.Length == 0) return null;
        if (Path.IsPathRooted(normalized)) return File.Exists(normalized) ? normalized : null;

        var defDir = Path.GetDirectoryName(defFull)!;
        foreach (var dir in new[] { defDir, root, Path.Combine(root, "data") })
        {
            var candidate = Path.Combine(dir, normalized);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? FindSimilarName(string root, string defFull, string reference)
    {
        var normalized = SelectDefReader.NormalizeSeparators(reference.Trim());
        var slash = normalized.LastIndexOf('/');
        var leaf = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        var subdir = slash >= 0 ? normalized[..slash] : string.Empty;
        var target = Loose(leaf);

        var defDir = Path.GetDirectoryName(defFull)!;
        foreach (var dir in new[] { defDir, root })
        {
            var searchDir = Path.Combine(dir, subdir);
            if (!Directory.Exists(searchDir)) continue;
            foreach (var file in Directory.EnumerateFiles(searchDir))
            {
                var name = Path.GetFileName(file);
                if (Loose(name) == target && !string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase))
                {
                    return slash >= 0 ? subdir + "/" + name : name;
                }
            }
        }

        return null;
    }

    private static string Loose(string name)
        => new string(name.ToLowerInvariant().Where(c => c is not ('\'' or '’' or '`' or '"')).ToArray());

    /// <param name="leadingBracketBreaks">
    /// Character roster lines start with the folder name, so a leading '[' would be read as a section
    /// header. Stage lines start with "stages/", so brackets are harmless there.
    /// </param>
    private static IEnumerable<ValidationIssue> ValidateName(string name, bool leadingBracketBreaks)
    {
        if (name.IndexOfAny(RosterBreakingChars) >= 0 || (leadingBracketBreaks && name.StartsWith('[')))
        {
            yield return new ValidationIssue(ValidationSeverity.Warning,
                "Name contains characters a select.def line cannot carry", name,
                "Avoid ',' ';' '\"' (and a leading '[' for character folders)");
        }

        // Harmless on Windows (IKEMEN handles them); reported for information only.
        if (name.Contains('\''))
        {
            yield return new ValidationIssue(ValidationSeverity.Info, "Filename contains apostrophe (')", name,
                "Apostrophes can cause path issues on other platforms");
        }

        if (name.Contains(' '))
        {
            yield return new ValidationIssue(ValidationSeverity.Info, "Filename contains spaces", name,
                "Spaces may cause issues with some configurations");
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
