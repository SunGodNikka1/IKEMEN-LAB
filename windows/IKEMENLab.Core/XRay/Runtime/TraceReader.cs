using System.Text;
using System.Text.Json;

namespace IKEMENLab.Core.XRay.Runtime;

/// <summary>
/// Reads the append-only JSONL trace. Tolerant by design: a truncated last line, stale bytes after the last newline, blank lines,
/// unknown event types and out-of-order frames become issues (or unknown events), never exceptions. Events must have strictly
/// non-decreasing <c>frame</c> values; an event that goes backwards is dropped and reported.
/// </summary>
public static class TraceReader
{
    public static TraceLog ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return Read(reader.ReadToEnd());
    }

    public static TraceLog Read(string text)
    {
        var events = new List<TraceEvent>();
        var issues = new List<TraceIssue>();
        TraceMeta? meta = null;
        long lastFrame = -1;
        long lastSample = -1;
        var lineNo = 0;

        // Lines end at '\n'. Text after the last '\n' is a line still being written (or stale bytes) and is only used if it parses.
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lineNo = i + 1;
            var line = lines[i].TrimEnd('\r', '\0').Trim();
            if (line.Length == 0) continue;
            var isLast = i == lines.Length - 1;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                issues.Add(new TraceIssue(lineNo, isLast ? "Ignored an incomplete final line (the writer was interrupted or stale bytes follow)." : "Ignored a line that is not valid JSON."));
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                {
                    issues.Add(new TraceIssue(lineNo, "Ignored a JSON line without a string \"type\"."));
                    continue;
                }

                var type = typeEl.GetString()!;
                var frame = Long(root, "frame");
                if (type == "meta")
                {
                    if (meta is null)
                    {
                        meta = ReadMeta(root);
                        events.Add(meta);
                    }
                    else
                    {
                        issues.Add(new TraceIssue(lineNo, "A second meta event was ignored."));
                    }

                    continue;
                }

                if (frame is null)
                {
                    issues.Add(new TraceIssue(lineNo, $"'{type}' event has no numeric frame; ignored."));
                    continue;
                }

                if (frame < lastFrame)
                {
                    issues.Add(new TraceIssue(lineNo, $"Frame {frame} goes back before frame {lastFrame}; event dropped."));
                    continue;
                }

                if (type == "frame" && lastSample == frame)
                {
                    issues.Add(new TraceIssue(lineNo, $"Duplicate frame sample {frame}; event dropped."));
                    continue;
                }
                if (type == "frame") lastSample = frame.Value;
                lastFrame = frame.Value;
                var engineTick = Long(root, "engineTick");
                events.Add(type switch
                {
                    "frame" => new FrameEvent(frame.Value, engineTick, Int(root, "round"), Player(root, "p1"), Player(root, "p2"),
                        Dbl(root, "distance"), Int(root, "p1TargetCount"), Int(root, "p1TargetId"), Int(root, "combo"), Str(root, "distanceSource")),
                    "state_change" => new StateChangeEvent(frame.Value, engineTick, Int(root, "player") ?? 0, Int(root, "from"), Int(root, "to")),
                    "life_change" => new LifeChangeEvent(frame.Value, engineTick, Int(root, "player") ?? 0, Dbl(root, "from"), Dbl(root, "to")),
                    "hit" => new HitEvent(frame.Value, engineTick, Int(root, "attacker"), Int(root, "defender"), Dbl(root, "lifeBefore"), Dbl(root, "lifeAfter")),
                    "input" => new InputEvent(frame.Value, engineTick, Int(root, "player") ?? 1, Strings(root, "keys"), Int(root, "step"), Str(root, "phase")),
                    "driver" => new DriverEvent(frame.Value, engineTick, Str(root, "event") ?? "unknown", Int(root, "step"), Str(root, "detail")),
                    "end" => new EndEvent(frame.Value, engineTick, root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null),
                    _ => new UnknownEvent(frame.Value, engineTick, type, line)
                });
            }
        }

        if (meta is null) issues.Add(new TraceIssue(1, "The trace has no meta event, so engine version and capabilities are unknown."));
        return new TraceLog { Meta = meta, Events = events, Issues = issues, LineCount = lineNo };
    }

    private static TraceMeta ReadMeta(JsonElement root)
    {
        var caps = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (root.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Object)
            foreach (var p in c.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) caps[p.Name] = p.Value.GetBoolean();

        var hooks = new List<string>();
        if (root.TryGetProperty("hooks", out var h) && h.ValueKind == JsonValueKind.Array)
            foreach (var e in h.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) hooks.Add(e.GetString()!);

        return new TraceMeta(Str(root, "schema"), Str(root, "engineVersion"), Str(root, "probeVersion"), Str(root, "character"),
            Str(root, "platform"), caps, hooks, Str(root, "planFingerprint"), Str(root, "engineSha256"), Str(root, "engineExecutable"), Str(root, "engineSource"));
    }

    private static PlayerSample Player(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object)
            return new PlayerSample(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        return new PlayerSample(
            Int(p, "state"), Int(p, "prevState"), Bool(p, "ctrl"), Str(p, "stateType"), Str(p, "moveType"), Int(p, "anim"), Int(p, "animElem"),
            Dbl(p, "life"), Dbl(p, "power"), Dbl(p, "x"), Dbl(p, "y"), Dbl(p, "velX"), Dbl(p, "velY"), Int(p, "facing"),
            Int(p, "moveHit"), Int(p, "moveContact"), Int(p, "hitPause"));
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString()!);
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < int.MaxValue ? (int)Math.Round(d) : null;

    private static double? Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static bool? Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when v.TryGetInt32(out var n) => n != 0,
            _ => null
        };
    }
}
