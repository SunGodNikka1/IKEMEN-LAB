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

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

        var matches = FindMatches(lines, ikemenRoot, section, targetKey);
        var active = matches.Where(m => !m.IsCommented).ToList();
        var disabled = matches.Where(m => m.IsCommented).ToList();

        if (active.Count > 1 || disabled.Count > 1)
        {
            return Fail(content,
                $"Ambiguous select.def entries for this {(kind == RosterContentKind.Character ? "character" : "stage")} " +
                $"({active.Count} active, {disabled.Count} disabled). Resolve duplicates manually.");
        }

        if (action == RosterToggleAction.Enable && active.Count >= 1 && disabled.Count >= 1)
        {
            return Fail(content,
                "Ambiguous select.def state: both active and disabled entries exist. Resolve duplicates manually.");
        }

        if (action == RosterToggleAction.Enable)
        {
            if (active.Count == 1 && disabled.Count == 0)
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

            if (disabled.Count == 1)
            {
                var idx = disabled[0].LineIndex;
                lines[idx] = UncommentLine(lines[idx]);
                var updated = string.Join(newline, lines);
                if (!EndsWithOriginalTerminator(content, newline) && content.EndsWith('\n'))
                {
                    // string.Join preserves last empty line if content ended with newline and Split kept trailing empty
                }

                return Validate(updated, ikemenRoot, kind, rootRelativeDefPath, ContentStatus.Active, idx + 1, "Uncommented existing entry.");
            }

            // Unregistered — append a new entry.
            var entryName = kind == RosterContentKind.Character
                ? PreferredCharacterRosterName(rootRelativeDefPath)
                : PreferredStageRosterName(rootRelativeDefPath);

            if (string.IsNullOrWhiteSpace(entryName))
            {
                return Fail(content, "Could not derive a safe select.def entry name.");
            }

            var inserted = InsertEntry(lines, section, entryName, newline);
            if (inserted.Error is not null)
            {
                return Fail(content, inserted.Error);
            }

            return Validate(inserted.Content!, ikemenRoot, kind, rootRelativeDefPath, ContentStatus.Active,
                inserted.LineNumber, $"Added roster entry '{entryName}'.");
        }

        // Disable
        if (active.Count == 0)
        {
            if (disabled.Count == 1)
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
            lines[idx] = CommentOutLine(lines[idx]);
            var updated = string.Join(newline, lines);
            return Validate(updated, ikemenRoot, kind, rootRelativeDefPath, ContentStatus.Disabled, idx + 1,
                "Commented out roster entry.");
        }
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
        public string? Content { get; init; }
        public string? Error { get; init; }
        public int? LineNumber { get; init; }
    }

    private static InsertResult InsertEntry(string[] lines, SelectDefSection section, string entryName, string newline)
    {
        var header = section == SelectDefSection.Characters ? "[Characters]" : "[ExtraStages]";
        var sectionStart = -1;
        var insertAt = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim(' ', '\t');
            if (!trimmed.StartsWith('[') || !trimmed.Contains(']')) continue;

            var end = trimmed.IndexOf(']');
            var name = trimmed[1..end].Trim();
            var isTarget = section == SelectDefSection.Characters
                ? name.Equals("Characters", StringComparison.OrdinalIgnoreCase)
                : name.Equals("ExtraStages", StringComparison.OrdinalIgnoreCase);
            if (!isTarget) continue;

            sectionStart = i;
            insertAt = i + 1;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var t = lines[j].Trim(' ', '\t');
                if (t.StartsWith('[') && t.Contains(']'))
                {
                    insertAt = j;
                    break;
                }

                insertAt = j + 1;
            }

            break;
        }

        var list = lines.ToList();
        if (sectionStart < 0)
        {
            // Append section at end.
            if (list.Count > 0 && !string.IsNullOrWhiteSpace(list[^1]))
                list.Add(string.Empty);
            list.Add(header);
            list.Add(entryName);
            return new InsertResult
            {
                Content = string.Join(newline, list),
                LineNumber = list.Count
            };
        }

        list.Insert(insertAt, entryName);
        return new InsertResult
        {
            Content = string.Join(newline, list),
            LineNumber = insertAt + 1
        };
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

    public static string UncommentLine(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        if (i < line.Length && line[i] == ';')
            return line[..i] + line[(i + 1)..];
        return line;
    }

    public static string NormalizeKey(string root, string relative)
    {
        var combined = Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return Path.GetFullPath(combined);
    }

    private static bool EndsWithOriginalTerminator(string content, string newline) => content.EndsWith(newline);

    private static RosterEditResult Fail(string content, string error)
        => new() { Success = false, Content = content, Error = error, Changed = false };
}
