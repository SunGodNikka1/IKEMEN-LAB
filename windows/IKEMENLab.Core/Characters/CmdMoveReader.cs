using System.Text;
using IKEMENLab.Core.Parsing;

namespace IKEMENLab.Core.Characters;

public sealed record CharacterMove(string Name, string DisplayName, string Command, string Notation, bool IsHyper);

/// <summary>
/// Extracts special/hyper motion commands from a .cmd file for the inspector's move list.
/// Unlike the macOS parser it reads each [Command] block's exact "name" and "command" keys (not
/// command.time/buffer.time), tokenises inputs case-sensitively (directions are upper case, buttons
/// lower case) and skips AI/CPU helper commands.
/// </summary>
public static class CmdMoveReader
{
    private static readonly string[] SkipPrefixes =
        ["ai", "cpu", "holdfwd", "holdback", "holdup", "holddown", "recovery", "fwd", "back", "up", "down", "longjump"];

    private static readonly HashSet<string> Directions = ["B", "DB", "D", "DF", "F", "UF", "U", "UB"];
    private static readonly HashSet<char> Buttons = ['a', 'b', 'c', 'x', 'y', 'z', 's', 'd', 'w'];

    public static IReadOnlyList<CharacterMove> ReadFile(string cmdPath)
    {
        var content = DefFileReader.ReadFileContent(cmdPath);
        return content is null ? Array.Empty<CharacterMove>() : Parse(content);
    }

    public static IReadOnlyList<CharacterMove> Parse(string content)
    {
        var moves = new List<CharacterMove>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? name = null, command = null;
        var inCommand = false;

        void Flush()
        {
            if (inCommand && name is not null && command is not null) Consider(name, command, moves, seen);
            name = null;
            command = null;
        }

        foreach (var raw in content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('['))
            {
                Flush();
                var end = line.IndexOf(']');
                var section = (end > 1 ? line[1..end] : line[1..]).Trim();
                inCommand = section.Equals("Command", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inCommand) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();
            if (key == "name") name = value.Trim('"').Trim();
            else if (key == "command") command = value;
        }

        Flush();

        return moves
            .OrderByDescending(m => m.IsHyper)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Consider(string name, string command, List<CharacterMove> moves, HashSet<string> seen)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Length == 0 || SkipPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal))) return;
        if (lower is "run" or "dash" or "jump") return;

        var steps = command.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (steps.Count < 2) return;
        // A motion input: at least two direction steps before the final button press.
        var directionSteps = steps.Take(steps.Count - 1).Count(s => Directions.Contains(StripModifiers(s)));
        var endsWithButton = IsButtonStep(StripModifiers(steps[^1]));
        if (directionSteps < 2 || !endsWithButton) return;

        if (!seen.Add(name)) return; // first definition per move name wins
        var isHyper = lower.Contains("hyper") || lower.Contains("super") || lower.Contains("ultimate");
        moves.Add(new CharacterMove(name, DisplayName(name), command, Notation(steps), isHyper));
    }

    private static string StripModifiers(string step)
    {
        var s = step.TrimStart('~', '/', '$', '>', ' ');
        // "~30$D" style: drop hold-time digits after ~
        var i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        return s[i..].TrimStart('$', '/', '>');
    }

    private static bool IsButtonStep(string step)
        => step.Length > 0 && step.Split('+').All(p => p.Length == 1 && Buttons.Contains(p[0]));

    public static string Notation(IReadOnlyList<string> steps)
    {
        var parts = new List<string>();
        foreach (var raw in steps)
        {
            var step = StripModifiers(raw);
            var tokens = step.Split('+').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            var rendered = tokens.Select(t => Directions.Contains(t) ? Arrow(t) : ButtonLabel(t));
            parts.Add(string.Join("+", rendered));
        }

        if (parts.Count >= 2 && IsButtonStep(StripModifiers(steps[^1])))
        {
            var motion = string.Concat(parts.Take(parts.Count - 1));
            return motion + " + " + parts[^1];
        }

        return string.Join(" ", parts);
    }

    private static string Arrow(string d) => d switch
    {
        "B" => "←",
        "DB" => "↙",
        "D" => "↓",
        "DF" => "↘",
        "F" => "→",
        "UF" => "↗",
        "U" => "↑",
        "UB" => "↖",
        _ => d
    };

    private static string ButtonLabel(string b) => b switch
    {
        "x" => "LP",
        "y" => "MP",
        "z" => "HP",
        "a" => "LK",
        "b" => "MK",
        "c" => "HK",
        "s" => "Start",
        _ => b.ToUpperInvariant()
    };

    /// <summary>"SpecialX" → "Special X", "QCF_x" → "QCF x".</summary>
    public static string DisplayName(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i] == '_' ? ' ' : name[i];
            if (i > 0 && (char.IsUpper(c) || char.IsDigit(c)))
            {
                var prev = name[i - 1];
                if (!char.IsUpper(prev) && !char.IsDigit(prev) && prev is not (' ' or '_')) sb.Append(' ');
            }

            sb.Append(c);
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (line[i] == ';' && !inQuote) return line[..i];
        }

        return line;
    }
}
