using System.Globalization;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Indexing;

public sealed record CommandStep(string Raw, IReadOnlyList<string> Keys, bool Release, int? ReleaseFrames, bool Hold, bool FourWay, bool Strict);

/// <summary>Indexes [Command] blocks (with [Defaults]) into <c>cmd:&lt;name&gt;</c> objects.</summary>
public static class CommandIndexer
{
    private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "B", "F", "U", "D", "DB", "DF", "UB", "UF" };

    /// <summary>Command name (case-insensitive) → id of the first definition.</summary>
    public static Dictionary<string, string> Index(IndexBuilder b, SourceFile file, IReadOnlyList<RawBlock> blocks)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaultTime = 15;
        var defaultBuffer = 1;

        foreach (var block in blocks)
        {
            if (block.Header.Equals("Defaults", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var e in block.Entries.Where(e => e.HadEquals))
                {
                    if (e.Key.Equals("command.time", StringComparison.OrdinalIgnoreCase) && Int(e.Value) is { } t) defaultTime = t;
                    if (e.Key.Equals("command.buffer.time", StringComparison.OrdinalIgnoreCase) && Int(e.Value) is { } bt) defaultBuffer = bt;
                }
            }
        }

        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            if (!block.Header.Equals("Command", StringComparison.OrdinalIgnoreCase)) continue;

            string? name = null, raw = null;
            int? time = null, buffer = null;
            int nameLine = block.HeaderLine;
            foreach (var e in block.Entries.Where(e => e.HadEquals))
            {
                switch (e.Key.ToLowerInvariant())
                {
                    case "name": name = e.Value.Trim('"', ' '); nameLine = e.Line; break;
                    case "command": raw = e.Value; break;
                    case "time": time = Int(e.Value); break;
                    case "buffer.time": buffer = Int(e.Value); break;
                }
            }

            var span = block.Span(file.Id);
            if (string.IsNullOrWhiteSpace(name))
            {
                b.Warn("command.no-name", "[Command] block without a name; skipped.", span);
                continue;
            }

            if (raw is null)
            {
                b.Warn("command.no-input", $"Command '{name}' has no command= line.", span);
                raw = string.Empty;
            }

            ordinals[name] = ordinals.TryGetValue(name, out var n) ? n + 1 : 1;
            var id = ordinals[name] == 1 ? $"cmd:{name}" : $"cmd:{name}#{ordinals[name]}";
            var steps = Tokenize(raw);
            var obj = b.Add(ObjectKind.Command, id, name, span, parent: null);
            obj.Props["raw"] = raw.Trim();
            obj.Props["steps"] = string.Join(" , ", steps.Select(s => s.Raw));
            obj.Props["time"] = (time ?? defaultTime).ToString(CultureInfo.InvariantCulture);
            obj.Props["buffer"] = (buffer ?? defaultBuffer).ToString(CultureInfo.InvariantCulture);
            obj.Props["directionSteps"] = steps.Count(s => s.Keys.Any(k => Directions.Contains(k))).ToString(CultureInfo.InvariantCulture);
            obj.Props["buttonSteps"] = steps.Count(s => s.Keys.Any(k => !Directions.Contains(k))).ToString(CultureInfo.InvariantCulture);
            try
            {
                obj.Props["notation"] = CmdMoveReader.Notation(steps.Select(s => s.Raw).ToList());
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                // Notation is a display nicety; the raw steps remain.
            }

            if (block.LeadingComments.Count > 0) obj.Props["comment"] = string.Join(" / ", block.LeadingComments);
            if (ordinals[name] > 1) obj.Props["alternateOf"] = $"cmd:{name}";
            byName.TryAdd(name, id);
        }

        return byName;
    }

    /// <summary>Splits a command definition into steps with their modifiers. Malformed steps are kept as-is.</summary>
    public static IReadOnlyList<CommandStep> Tokenize(string raw)
    {
        var steps = new List<CommandStep>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (t.Length == 0) continue;
            var i = 0;
            bool release = false, hold = false, fourWay = false, strict = false;
            int? releaseFrames = null;
            while (i < t.Length && t[i] is '~' or '/' or '$' or '>')
            {
                switch (t[i])
                {
                    case '~':
                        release = true;
                        var j = i + 1;
                        while (j < t.Length && char.IsDigit(t[j])) j++;
                        if (j > i + 1) releaseFrames = int.Parse(t[(i + 1)..j], CultureInfo.InvariantCulture);
                        i = j - 1;
                        break;
                    case '/': hold = true; break;
                    case '$': fourWay = true; break;
                    case '>': strict = true; break;
                }

                i++;
            }

            var keys = t[i..].Split('+', StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
            steps.Add(new CommandStep(t, keys, release, releaseFrames, hold, fourWay, strict));
        }

        return steps;
    }

    private static int? Int(string v) =>
        int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
