using System.Globalization;

namespace IKEMENLab.Core.Characters;

public sealed record CnsEdit(string Section, string Key, string Value);

public sealed class CnsEditResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? Content { get; init; }
}

/// <summary>
/// Line-level CNS editing for the few [Data]/[Size] keys the tuning editor touches. Every line the edit
/// does not concern keeps its exact text and terminator; an edited line keeps its key spelling, spacing
/// and trailing comment. Where a key is repeated, the last one is the one that counts (as when IKEMEN reads
/// the file), so that is the one changed. A missing key is added at the end of its section; a missing
/// section is added before the first Statedef, where MUGEN and IKEMEN expect the header sections.
/// </summary>
public static class CnsEditor
{
    /// <summary>Values by lower-cased (section, key); the last assignment wins and comments are removed.</summary>
    public static IReadOnlyDictionary<(string Section, string Key), string> ReadValues(string text)
    {
        var values = new Dictionary<(string, string), string>();
        string? section = null;
        foreach (var line in Split(text))
        {
            var t = Clean(line.Text);
            if (t.Length == 0 || t.StartsWith(';')) continue;
            if (SectionName(t) is { } name) { section = name.ToLowerInvariant(); continue; }
            if (section is null) continue;
            var eq = t.IndexOf('=');
            if (eq <= 0) continue;
            var value = t[(eq + 1)..];
            var comment = value.IndexOf(';');
            if (comment >= 0) value = value[..comment];
            values[(section, t[..eq].Trim().ToLowerInvariant())] = value.Trim();
        }

        return values;
    }

    public static CnsEditResult SetValues(string text, IReadOnlyList<CnsEdit> edits)
    {
        if (string.IsNullOrEmpty(text)) return Fail("The CNS file is empty.");
        var lines = Split(text);
        var newline = lines.FirstOrDefault(l => l.Terminator.Length > 0)?.Terminator ?? "\n";

        foreach (var edit in edits)
        {
            if (edit.Value.IndexOfAny(['\r', '\n', ';']) >= 0)
                return Fail($"'{edit.Value}' is not a valid value for {edit.Key}.");

            var (keyLine, lastSectionLine) = Locate(lines, edit.Section, edit.Key);
            if (keyLine >= 0)
            {
                lines[keyLine].Text = ReplaceValue(lines[keyLine].Text, edit.Value);
            }
            else if (lastSectionLine >= 0)
            {
                Insert(lines, lastSectionLine + 1, $"{edit.Key} = {edit.Value}", newline);
            }
            else
            {
                var at = lines.FindIndex(l => Clean(l.Text).StartsWith("[statedef", StringComparison.OrdinalIgnoreCase));
                if (at < 0) at = lines.Count;
                // Keep a blank line between the new block and what follows it.
                Insert(lines, at, string.Empty, newline);
                Insert(lines, at, $"{edit.Key} = {edit.Value}", newline);
                Insert(lines, at, $"[{edit.Section}]", newline);
            }
        }

        return new CnsEditResult { Success = true, Content = string.Concat(lines.Select(l => l.Text + l.Terminator)) };
    }

    /// <summary>(last line of the key in any instance of the section, last non-blank line of the section's last instance).</summary>
    private static (int KeyLine, int SectionEnd) Locate(List<Line> lines, string section, string key)
    {
        var keyLine = -1;
        var sectionEnd = -1;
        var inSection = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = Clean(lines[i].Text);
            if (t.Length == 0 || t.StartsWith(';')) continue;
            if (SectionName(t) is { } name)
            {
                inSection = name.Equals(section, StringComparison.OrdinalIgnoreCase);
                if (inSection) sectionEnd = i;
                continue;
            }

            if (!inSection) continue;
            sectionEnd = i;
            var eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) keyLine = i;
        }

        return (keyLine, sectionEnd);
    }

    private static void Insert(List<Line> lines, int index, string text, string newline)
    {
        // Appending after an unterminated last line: terminate it and keep the file's no-final-newline form.
        var atEnd = index >= lines.Count;
        var unterminatedEnd = atEnd && lines.Count > 0 && lines[^1].Terminator.Length == 0;
        if (unterminatedEnd) lines[^1].Terminator = newline;
        lines.Insert(index, new Line(text, unterminatedEnd ? string.Empty : newline));
    }

    private static string ReplaceValue(string line, string value)
    {
        var eq = line.IndexOf('=');
        var afterEq = line[(eq + 1)..];
        var lead = afterEq.Length - afterEq.TrimStart(' ', '\t').Length;
        var rest = afterEq[lead..];
        var comment = rest.IndexOf(';');
        var body = comment >= 0 ? rest[..comment] : rest;
        var tail = comment >= 0 ? rest[body.TrimEnd(' ', '\t').Length..] : string.Empty;
        return line[..(eq + 1)] + afterEq[..lead] + value + tail;
    }

    public static string FormatNumber(double value, bool isInteger) =>
        isInteger
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string Clean(string s) => s.Trim(' ', '\t', '﻿');

    private static string? SectionName(string trimmed)
    {
        if (!trimmed.StartsWith('[')) return null;
        var end = trimmed.IndexOf(']');
        return end > 0 ? trimmed[1..end].Trim() : null;
    }

    private static CnsEditResult Fail(string error) => new() { Success = false, Error = error };

    private sealed class Line(string text, string terminator)
    {
        public string Text { get; set; } = text;
        public string Terminator { get; set; } = terminator;
    }

    private static List<Line> Split(string content)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c != '\r' && c != '\n') continue;
            var terminator = c == '\r' && i + 1 < content.Length && content[i + 1] == '\n' ? "\r\n" : c.ToString();
            lines.Add(new Line(content[start..i], terminator));
            i += terminator.Length - 1;
            start = i + 1;
        }

        if (start < content.Length) lines.Add(new Line(content[start..], string.Empty));
        return lines;
    }
}
