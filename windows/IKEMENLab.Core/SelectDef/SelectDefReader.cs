using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.SelectDef;

/// <summary>
/// Read-only select.def parser that follows IKEMEN GO's own roster resolution rules.
/// Deliberately avoids the macOS parser's defects: backslash separators are normalised,
/// nested "pack/folder/def" entries resolve correctly, and inline ';' comments are stripped.
/// </summary>
public static class SelectDefReader
{
    public static SelectDefDocument? ReadFile(string path, string? rootForResolution = null)
    {
        var content = DefFileReader.ReadFileContent(path);
        if (content is null) return null;

        var entries = Parse(content);
        if (rootForResolution is not null)
        {
            entries = entries.Select(e => Resolve(e, rootForResolution)).ToList();
        }

        return new SelectDefDocument { Path = path, Entries = entries };
    }

    public static IReadOnlyList<SelectDefEntry> Parse(string content)
    {
        var entries = new List<SelectDefEntry>();
        SelectDefSection? section = null;
        var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim(' ', '\t', '﻿');
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith('['))
            {
                var end = trimmed.IndexOf(']');
                var name = (end > 1 ? trimmed[1..end] : trimmed[1..]).Trim().ToLowerInvariant();
                section = name switch
                {
                    "characters" => SelectDefSection.Characters,
                    "extrastages" => SelectDefSection.ExtraStages,
                    _ => null
                };
                continue;
            }

            if (section is null) continue;

            var isCommented = trimmed.StartsWith(';');
            var body = isCommented ? trimmed.TrimStart(';').TrimStart(' ', '\t') : trimmed;

            // Strip inline comments (IKEMEN drops everything after ';').
            var comment = body.IndexOf(';');
            if (comment >= 0) body = body[..comment];

            var first = body.Split(',')[0].Trim(' ', '\t');
            if (first.Length == 0) continue;

            entries.Add(section == SelectDefSection.Characters
                ? BuildCharacterEntry(first, i + 1, isCommented)
                : BuildStageEntry(first, i + 1, isCommented));
        }

        return entries;
    }

    /// <summary>
    /// IKEMEN character resolution: "kfm" → kfm/kfm.def, "pack/kfm" → pack/kfm.def,
    /// "pack/kfm.def" as-is, bare "kfm.def" is rejected. Each form is looked up
    /// relative to the root first, then under chars/.
    /// </summary>
    public static (SelectDefEntryKind Kind, IReadOnlyList<string> Candidates) ResolveCharacterName(string rawName)
    {
        var def = NormalizeSeparators(rawName.Trim());
        var lower = def.ToLowerInvariant();

        if (lower == "randomselect") return (SelectDefEntryKind.RandomSelect, Array.Empty<string>());
        if (lower == "empty") return (SelectDefEntryKind.Empty, Array.Empty<string>());
        if (def.Length == 0) return (SelectDefEntryKind.Invalid, Array.Empty<string>());

        var slash = def.IndexOf('/');
        if (lower.EndsWith(".def", StringComparison.Ordinal))
        {
            if (slash < 0) return (SelectDefEntryKind.Invalid, Array.Empty<string>());
        }
        else if (slash < 0)
        {
            def = def + "/" + def + ".def";
        }
        else
        {
            def += ".def";
        }

        if (IsAbsolute(def) || def.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
        {
            return (SelectDefEntryKind.Content, [def]);
        }

        return (SelectDefEntryKind.Content, [def, "chars/" + def]);
    }

    public static IReadOnlyList<string> ResolveStagePath(string rawPath)
    {
        var def = NormalizeSeparators(rawPath.Trim());
        if (def.Length == 0) return Array.Empty<string>();
        if (IsAbsolute(def) || def.StartsWith("stages/", StringComparison.OrdinalIgnoreCase))
        {
            return [def];
        }

        return [def, "stages/" + def];
    }

    /// <summary>Returns a copy with <see cref="SelectDefEntry.ResolvedDefPath"/> set to the first existing candidate.</summary>
    public static SelectDefEntry Resolve(SelectDefEntry entry, string root)
    {
        foreach (var candidate in entry.CandidateDefPaths)
        {
            var full = IsAbsolute(candidate) ? candidate : Path.Combine(root, candidate);
            if (File.Exists(full))
            {
                return entry with { ResolvedDefPath = candidate };
            }
        }

        foreach (var candidate in entry.CandidateDefPaths)
        {
            var archive = ArchivePrefix(candidate);
            if (archive is null) continue;
            var full = IsAbsolute(archive) ? archive : Path.Combine(root, archive);
            if (File.Exists(full))
            {
                return entry with { IsInArchive = true };
            }
        }

        return entry;
    }

    public static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    /// <summary>"stages/Pack.zip/Pack/x.def" → "stages/Pack.zip"; null when no segment is a .zip.</summary>
    private static string? ArchivePrefix(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join('/', segments.Take(i + 1));
            }
        }

        return null;
    }

    private static bool IsAbsolute(string path)
        => path.StartsWith('/') || (path.Length >= 2 && path[1] == ':');

    private static SelectDefEntry BuildCharacterEntry(string first, int line, bool commented)
    {
        var (kind, candidates) = ResolveCharacterName(first);
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

    private static SelectDefEntry BuildStageEntry(string first, int line, bool commented)
    {
        var candidates = ResolveStagePath(first);
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
}
