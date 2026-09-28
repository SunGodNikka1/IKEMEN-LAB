using IKEMENLab.Core.Models;

namespace IKEMENLab.Core.SelectDef;

public enum RosterToggleAction
{
    Enable,
    Disable
}

public enum RosterContentKind
{
    Character,
    Stage
}

public sealed class RosterEditResult
{
    public required bool Success { get; init; }
    public required string Content { get; init; }
    public string? Error { get; init; }
    public string? Description { get; init; }
    public bool Changed { get; init; }
    public int? AffectedLineNumber { get; init; }
}

/// <summary>
/// Narrow line-level select.def edits. Never regenerates the whole document.
/// Preserves comments, blank lines, parameters, and non-roster sections.
/// </summary>
public static class SelectDefRosterEditor
{
    public static RosterEditResult Toggle(
        string content,
        string ikemenRoot,
        RosterContentKind kind,
        string rootRelativeDefPath,
        RosterToggleAction action)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Fail(content, "select.def is empty or unreadable.");
        }

        var targetKey = NormalizeKey(ikemenRoot, rootRelativeDefPath);
        var section = kind == RosterContentKind.Character
            ? SelectDefSection.Characters
            : SelectDefSection.ExtraStages;
        var what = kind == RosterContentKind.Character ? "character" : "stage";

        // Work on line spans so an edit touches exactly one line: every other byte, the original
        // newline style (including mixed endings) and the final terminator stay as they were.
        var spans = SplitLines(content);
        var lines = spans.Select(s => content.Substring(s.Start, s.Length)).ToArray();

        var matches = FindMatches(lines, ikemenRoot, section, targetKey);
        var active = matches.Where(m => !m.IsCommented).ToList();
        var disabled = matches.Where(m => m.IsCommented).ToList();

        // Several *active* lines for one item stay fail-closed (which slot to keep is the user's call).
        // Commented duplicates no longer block anything: previously disabling an entry that already had
        // a commented copy produced two commented lines and the item could never be re-enabled.
        if (active.Count > 1)
        {
            return Fail(content,
                $"Ambiguous select.def entries: this {what} is listed {active.Count} times (lines " +
                string.Join(", ", active.Select(m => m.LineIndex + 1)) +
                "). Remove the extra entries manually, then try again.");
        }

        if (action == RosterToggleAction.Enable)
        {
            if (active.Count == 1)
            {
                return new RosterEditResult
                {
                    Success = true,
                    Content = content,
                    Changed = false,
                    Description = "Already enabled.",
                    AffectedLineNumber = active[0].LineIndex + 1
                };
            }

            if (disabled.Count >= 1)
            {
                // Re-enable the first commented entry in roster order; any further commented
                // duplicates stay commented (they do not affect the resulting status).
                var idx = disabled[0].LineIndex;
                var updated = ReplaceLine(content, spans[idx], UncommentLine(lines[idx]));
                var description = disabled.Count > 1
                    ? $"Uncommented existing entry (line {idx + 1}); {disabled.Count - 1} other commented duplicate(s) left as-is."
                    : "Uncommented existing entry.";
                return Validate(updated, ikemenRoot, kind, rootRelativeDefPath, ContentStatus.Active, idx + 1, description);
            }

            // Unregistered — add a new entry at the end of the section.
            var entryName = kind == RosterContentKind.Character
                ? PreferredCharacterRosterName(rootRelativeDefPath)
                : PreferredStageRosterName(rootRelativeDefPath);

            if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOfAny([',', ';']) >= 0 ||
                (kind == RosterContentKind.Character && entryName.StartsWith('[')))
            {
                return Fail(content, $"This {what}'s path cannot be written as a select.def entry (it contains ',', ';' or a leading '[').");
            }

            var inserted = InsertEntry(content, spans, lines, section, entryName);
            return Validate(inserted.Content, ikemenRoot, kind, rootRelativeDefPath, ContentStatus.Active,
                inserted.LineNumber, $"Added roster entry '{entryName}'.");
        }

        // Disable
        if (active.Count == 0)
        {
            if (disabled.Count >= 1)
            {
                return new RosterEditResult
                {
                    Success = true,
                    Content = content,
                    Changed = false,
                    Description = "Already disabled.",
                    AffectedLineNumber = disabled[0].LineIndex + 1
                };
            }

            return Fail(content, "No active roster entry to disable.");
        }

        {
            var idx = active[0].LineIndex;
            var updated = ReplaceLine(content, spans[idx], CommentOutLine(lines[idx]));
            return Validate(updated, ikemenRoot, kind, rootRelativeDefPath, ContentStatus.Disabled, idx + 1,
                "Commented out roster entry.");
        }
    }

    private readonly record struct LineSpan(int Start, int Length, int TerminatorLength);

    private static List<LineSpan> SplitLines(string content)
    {
        var spans = new List<LineSpan>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\r')
            {
                var term = i + 1 < content.Length && content[i + 1] == '\n' ? 2 : 1;
                spans.Add(new LineSpan(start, i - start, term));
                i += term - 1;
                start = i + 1;
            }
            else if (content[i] == '\n')
            {
                spans.Add(new LineSpan(start, i - start, 1));
                start = i + 1;
            }
        }

        spans.Add(new LineSpan(start, content.Length - start, 0));
        return spans;
    }

    private static string ReplaceLine(string content, LineSpan span, string newText)
        => string.Concat(content.AsSpan(0, span.Start), newText, content.AsSpan(span.Start + span.Length));

    private static string PreferredNewline(string content, IReadOnlyList<LineSpan> spans, int nearIndex)
    {
        for (var i = Math.Min(nearIndex, spans.Count - 1); i >= 0; i--)
        {
            if (spans[i].TerminatorLength > 0)
                return content.Substring(spans[i].Start + spans[i].Length, spans[i].TerminatorLength);
        }

        return content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : content.Contains('\n') ? "\n" : "\r\n";
    }

    private static RosterEditResult Validate(
        string updated,
        string ikemenRoot,
        RosterContentKind kind,
        string rootRelativeDefPath,
        ContentStatus expected,
        int? lineNumber,
        string description)
    {
        try
        {
            var entries = SelectDefReader.Parse(updated);
            // Ensure parse succeeded and section still exists.
            _ = entries;
        }
        catch (Exception ex)
        {
            return Fail(updated, "Proposed select.def failed to parse: " + ex.Message);
        }

        // Build a temporary in-memory status check via resolve.
        var status = ProbeStatus(updated, ikemenRoot, kind, rootRelativeDefPath);
        if (status != expected)
        {
            return Fail(updated,
                $"Post-edit status would be {status}, expected {expected}.");
        }

        return new RosterEditResult
        {
            Success = true,
            Content = updated,
            Changed = true,
            Description = description,
            AffectedLineNumber = lineNumber
        };
    }

    public static ContentStatus ProbeStatus(
        string content,
        string ikemenRoot,
        RosterContentKind kind,
        string rootRelativeDefPath)
    {
        var targetKey = NormalizeKey(ikemenRoot, rootRelativeDefPath);
        var section = kind == RosterContentKind.Character
            ? SelectDefSection.Characters
            : SelectDefSection.ExtraStages;
        var entries = SelectDefReader.Parse(content)
            .Where(e => e.Section == section && e.Kind == SelectDefEntryKind.Content)
            .Select(e => SelectDefReader.Resolve(e, ikemenRoot))
            .Where(e => e.ResolvedDefPath is not null &&
                        string.Equals(NormalizeKey(ikemenRoot, e.ResolvedDefPath!), targetKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (entries.Any(e => !e.IsCommented)) return ContentStatus.Active;
        if (entries.Any(e => e.IsCommented)) return ContentStatus.Disabled;
        return ContentStatus.Unregistered;
    }

    private sealed record Match(int LineIndex, bool IsCommented);

    private static List<Match> FindMatches(string[] lines, string root, SelectDefSection section, string targetKey)
    {
        var matches = new List<Match>();
        SelectDefSection? current = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim(' ', '\t', '\uFEFF');
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith('['))
            {
                var end = trimmed.IndexOf(']');
                var name = (end > 1 ? trimmed[1..end] : trimmed[1..]).Trim().ToLowerInvariant();
                current = name switch
                {
                    "characters" => SelectDefSection.Characters,
                    "extrastages" => SelectDefSection.ExtraStages,
                    _ => null
                };
                continue;
            }

            if (current != section) continue;

            var isCommented = trimmed.StartsWith(';');
            var body = isCommented ? trimmed.TrimStart(';').TrimStart(' ', '\t') : trimmed;
            var comment = body.IndexOf(';');
            if (comment >= 0) body = body[..comment];
            var first = body.Split(',')[0].Trim(' ', '\t');
            if (first.Length == 0) continue;

            var entry = section == SelectDefSection.Characters
                ? BuildChar(first, i + 1, isCommented)
                : BuildStage(first, i + 1, isCommented);

            if (entry.Kind != SelectDefEntryKind.Content) continue;
            entry = SelectDefReader.Resolve(entry, root);
            if (entry.ResolvedDefPath is null) continue;
            if (!string.Equals(NormalizeKey(root, entry.ResolvedDefPath), targetKey, StringComparison.OrdinalIgnoreCase))
                continue;

            matches.Add(new Match(i, isCommented));
        }

        return matches;
    }

    private static SelectDefEntry BuildChar(string first, int line, bool commented)
    {
        var (kind, candidates) = SelectDefReader.ResolveCharacterName(first);
        return new SelectDefEntry
        {
            Section = SelectDefSection.Characters,
            LineNumber = line,
            RawName = first,
            Kind = kind,
            IsCommented = commented,
            CandidateDefPaths = candidates
        };
    }

    private static SelectDefEntry BuildStage(string first, int line, bool commented)
    {
        var candidates = SelectDefReader.ResolveStagePath(first);
        return new SelectDefEntry
        {
            Section = SelectDefSection.ExtraStages,
            LineNumber = line,
            RawName = first,
            Kind = candidates.Count == 0 ? SelectDefEntryKind.Invalid : SelectDefEntryKind.Content,
            IsCommented = commented,
            CandidateDefPaths = candidates
        };
    }

    private sealed class InsertResult
    {
        public required string Content { get; init; }
        public int? LineNumber { get; init; }
    }

    /// <summary>
    /// Inserts <paramref name="entryName"/> after the last non-blank line of the section (so trailing
    /// blank lines before the next header are kept), or appends a new section when none exists.
    /// </summary>
    private static InsertResult InsertEntry(
        string content, IReadOnlyList<LineSpan> spans, string[] lines, SelectDefSection section, string entryName)
    {
        var sectionStart = -1;
        var sectionEnd = lines.Length; // exclusive
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim(' ', '\t', '﻿');
            if (!trimmed.StartsWith('[') || !trimmed.Contains(']')) continue;

            if (sectionStart >= 0)
            {
                sectionEnd = i;
                break;
            }

            var end = trimmed.IndexOf(']');
            var name = trimmed[1..end].Trim();
            var isTarget = section == SelectDefSection.Characters
                ? name.Equals("Characters", StringComparison.OrdinalIgnoreCase)
                : name.Equals("ExtraStages", StringComparison.OrdinalIgnoreCase);
            if (isTarget) sectionStart = i;
        }

        if (sectionStart < 0)
        {
            var header = section == SelectDefSection.Characters ? "[Characters]" : "[ExtraStages]";
            var nl = PreferredNewline(content, spans, spans.Count - 1);
            var prefix = content.Length == 0 || content.EndsWith('\n') || content.EndsWith('\r') ? string.Empty : nl;
            var appended = content + prefix + nl + header + nl + entryName + nl;
            return new InsertResult { Content = appended, LineNumber = SplitLines(appended).Count - 1 };
        }

        var anchor = sectionStart;
        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i])) anchor = i;
        }

        var newline = PreferredNewline(content, spans, anchor);
        var anchorSpan = spans[anchor];
        string updated;
        if (anchorSpan.TerminatorLength > 0)
        {
            var at = anchorSpan.Start + anchorSpan.Length + anchorSpan.TerminatorLength;
            updated = string.Concat(content.AsSpan(0, at), entryName + newline, content.AsSpan(at));
        }
        else
        {
            // Anchor is the final line without a terminator.
            updated = content + newline + entryName;
        }

        return new InsertResult { Content = updated, LineNumber = anchor + 2 };
    }

    public static string PreferredCharacterRosterName(string rootRelativeDefPath)
    {
        var norm = SelectDefReader.NormalizeSeparators(rootRelativeDefPath).Trim();
        if (norm.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
            norm = norm["chars/".Length..];

        var withDef = norm;
        if (!withDef.EndsWith(".def", StringComparison.OrdinalIgnoreCase))
            withDef += ".def";

        if (norm.EndsWith(".def", StringComparison.OrdinalIgnoreCase))
            norm = norm[..^4];

        var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;

        // Prefer short form when IKEMEN resolution maps back to the same DEF
        // (e.g. kfm/kfm.def → "kfm"). Do NOT collapse Pack/Ken/Ken → Pack/Ken.
        if (parts.Length >= 2 &&
            string.Equals(parts[^1], parts[^2], StringComparison.OrdinalIgnoreCase))
        {
            var collapsed = string.Join('/', parts.Take(parts.Length - 1));
            var (_, candidates) = SelectDefReader.ResolveCharacterName(collapsed);
            var wantChars = "chars/" + withDef.TrimStart('/');
            var wantBare = withDef.TrimStart('/');
            if (candidates.Any(c =>
                    c.Equals(wantChars, StringComparison.OrdinalIgnoreCase) ||
                    c.Equals(wantBare, StringComparison.OrdinalIgnoreCase)))
            {
                return collapsed;
            }
        }

        return string.Join('/', parts);
    }

    public static string PreferredStageRosterName(string rootRelativeDefPath)
    {
        var norm = SelectDefReader.NormalizeSeparators(rootRelativeDefPath).Trim();
        if (!norm.StartsWith("stages/", StringComparison.OrdinalIgnoreCase) &&
            !Path.IsPathRooted(norm))
        {
            norm = "stages/" + norm.TrimStart('/');
        }

        return norm;
    }

    public static string CommentOutLine(string line)
    {
        var trimmed = line.TrimStart(' ', '\t');
        if (trimmed.StartsWith(';')) return line;
        return ";" + line;
    }

    /// <summary>Removes every leading ';' (after indentation), so ";;kfm" re-enables as "kfm".</summary>
    public static string UncommentLine(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        var j = i;
        while (j < line.Length && line[j] == ';') j++;
        return j == i ? line : line[..i] + line[j..];
    }

    public static string NormalizeKey(string root, string relative)
    {
        var combined = Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return Path.GetFullPath(combined);
    }

    private static RosterEditResult Fail(string content, string error)
        => new() { Success = false, Content = content, Error = error, Changed = false };
}
