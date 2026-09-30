namespace IKEMENLab.Core.XRay.Source;

/// <summary>
/// Tolerant line lexer for MUGEN/IKEMEN text files (DEF, CMD, CNS, ST, AIR). Never throws: anything it cannot
/// make sense of becomes a diagnostic or a bare entry. Line numbers count every CR, LF or CRLF as one break.
/// </summary>
public static class SourceLexer
{
    public static LexResult Lex(string text, int fileId)
    {
        var result = new LexResult();
        RawBlock? current = null;
        var pending = new List<string>();
        var lineNo = 0;

        foreach (var raw in SplitLines(text))
        {
            lineNo++;
            var line = lineNo == 1 ? raw.TrimStart('﻿') : raw;
            var (code, comment) = SplitComment(line);
            var trimmed = code.Trim(' ', '\t');

            if (trimmed.Length == 0)
            {
                if (comment is not null)
                {
                    // A comment-only line: remember it as a possible name for the next header.
                    pending.Add(comment!.Trim());
                }
                else
                {
                    pending.Clear(); // a blank line ends the run of leading comments
                }

                if (current is not null) current.EndLine = lineNo;
                continue;
            }

            if (trimmed[0] == '[')
            {
                var close = trimmed.IndexOf(']');
                string header;
                if (close < 0)
                {
                    header = trimmed[1..].Trim();
                    result.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "lex.unclosed-bracket",
                        $"Section header '{trimmed}' has no closing ']'.", SourceRef.At(fileId, lineNo)));
                }
                else
                {
                    header = trimmed[1..close].Trim();
                }

                current = new RawBlock { Header = header, HeaderLine = lineNo, EndLine = lineNo };
                current.LeadingComments.AddRange(pending);
                pending.Clear();
                result.Blocks.Add(current);
                continue;
            }

            pending.Clear();
            if (current is null)
            {
                current = new RawBlock { Header = string.Empty, HeaderLine = lineNo, EndLine = lineNo };
                result.Blocks.Add(current);
            }

            var eq = IndexOfOutsideQuotes(code, '=');
            current.Entries.Add(eq < 0
                ? new RawEntry(trimmed, string.Empty, lineNo, false, comment)
                : new RawEntry(code[..eq].Trim(' ', '\t'), code[(eq + 1)..].Trim(' ', '\t'), lineNo, true, comment));
            current.EndLine = lineNo;
        }

        return result;
    }

    /// <summary>Splits on CRLF, LF or lone CR, so mixed-ending files keep true line numbers.</summary>
    public static IEnumerable<string> SplitLines(string text)
    {
        if (text.Length == 0) yield break;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            yield return text[start..i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }

        if (start < text.Length) yield return text[start..];
    }

    /// <summary>Removes a trailing <c>; comment</c> (a ';' inside double quotes is data). Comment is null when there is none.</summary>
    public static (string Code, string? Comment) SplitComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes) return (line[..i], line[(i + 1)..]);
        }

        return (line, null);
    }

    private static int IndexOfOutsideQuotes(string s, char target)
    {
        var inQuotes = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') inQuotes = !inQuotes;
            else if (s[i] == target && !inQuotes) return i;
        }

        return -1;
    }
}
