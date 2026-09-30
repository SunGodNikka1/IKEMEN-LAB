using System.Globalization;
using System.Text.RegularExpressions;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Indexing;

public sealed record SpriteFacts(int Width, int Height, int AxisX, int AxisY);

/// <summary>
/// Indexes [Begin Action n] blocks: frames (sprite, offset, duration, flip), Clsn1 (attack) and Clsn2 (hurt) boxes with
/// Default inheritance, and LoopStart. Sprite references are checked against the SFF when one is available.
/// </summary>
public static partial class AirIndexer
{
    [GeneratedRegex(@"^begin\s+action\s+(-?\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ActionHeader();

    [GeneratedRegex(@"^clsn([12])(default)?\s*:\s*(-?\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ClsnCount();

    [GeneratedRegex(@"^clsn([12])(default)?\s*\[\s*(\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex ClsnBox();

    public static void Index(IndexBuilder b, SourceFile file, IReadOnlyList<RawBlock> blocks,
        IReadOnlyDictionary<(int Group, int Index), SpriteFacts>? sprites)
    {
        var ordinals = new Dictionary<int, int>();
        foreach (var block in blocks)
        {
            var m = ActionHeader().Match(block.Header);
            if (!m.Success)
            {
                if (block.Header.Length > 0)
                    b.Warn("air.unknown-block", $"AIR block '[{block.Header}]' is not an action; ignored.", block.Span(file.Id), DiagnosticSeverity.Info);
                continue;
            }

            var number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            ordinals[number] = ordinals.TryGetValue(number, out var n) ? n + 1 : 1;
            var animId = ordinals[number] == 1 ? $"anim:{number}" : $"anim:{number}#{ordinals[number]}";
            var span = block.Span(file.Id);
            if (ordinals[number] > 1) b.Warn("air.duplicate-action", $"Action {number} is defined more than once.", span);

            var anim = b.Add(ObjectKind.Animation, animId, $"Action {number}", span);
            if (block.LeadingComments.Count > 0) anim.Props["comment"] = string.Join(" / ", block.LeadingComments);

            // Current box collections per type. Default sets persist; local sets apply to the next frame only.
            var defaults = new[] { new List<int[]>(), new List<int[]>() };
            var locals = new[] { new List<int[]>(), new List<int[]>() };
            var localDeclared = new[] { false, false };
            var writingDefault = new[] { false, false };

            var frameIndex = 0;
            var tick = 0;
            var infinite = false;
            int? loopStart = null;

            foreach (var e in block.Entries)
            {
                var key = e.HadEquals ? e.Key : e.Key;
                var line = SourceRef.At(file.Id, e.Line);

                if (!e.HadEquals && key.Equals("LoopStart", StringComparison.OrdinalIgnoreCase))
                {
                    loopStart = frameIndex;
                    continue;
                }

                var count = ClsnCount().Match(key);
                if (count.Success && !e.HadEquals || count.Success && e.HadEquals && e.Value.Length == 0)
                {
                    var t = int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
                    var isDefault = count.Groups[2].Success;
                    writingDefault[t] = isDefault;
                    if (isDefault) defaults[t].Clear(); else { locals[t].Clear(); localDeclared[t] = true; }
                    continue;
                }

                // "Clsn2Default: 2" is written with a colon rather than '=', so the lexer keeps it as a bare line.
                var colon = key.IndexOf(':');
                if (!e.HadEquals && colon > 0)
                {
                    var head = key[..colon].Trim();
                    var c2 = ClsnCount().Match(head + ": " + key[(colon + 1)..].Trim());
                    if (c2.Success)
                    {
                        var t = int.Parse(c2.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
                        var isDefault = c2.Groups[2].Success;
                        writingDefault[t] = isDefault;
                        if (isDefault) defaults[t].Clear(); else { locals[t].Clear(); localDeclared[t] = true; }
                        continue;
                    }
                }

                if (e.HadEquals && ClsnBox().Match(key) is { Success: true } box)
                {
                    var t = int.Parse(box.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
                    var nums = Numbers(e.Value);
                    if (nums is null || nums.Length < 4)
                    {
                        b.Warn("air.bad-clsn", $"Collision box '{key} = {e.Value}' needs four numbers.", line);
                        continue;
                    }

                    (writingDefault[t] || box.Groups[2].Success ? defaults[t] : locals[t]).Add(nums);
                    continue;
                }

                if (e.HadEquals)
                {
                    b.Warn("air.unknown-line", $"Unrecognised AIR line '{key} = {e.Value}'.", line, DiagnosticSeverity.Info);
                    continue;
                }

                // Frame line: group,index,x,y,time[,flip[,trans[,xscale,yscale,angle]]]
                var parts = key.Split(',').Select(p => p.Trim()).ToArray();
                if (parts.Length < 5 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                    !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                {
                    b.Warn("air.bad-frame", $"Cannot read animation frame '{key}'.", line);
                    continue;
                }

                _ = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ox);
                _ = int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var oy);

                var frameId = $"{animId}/frame:{frameIndex}";
                var frame = b.Add(ObjectKind.AnimFrame, frameId, $"Frame {frameIndex}", line, animId);
                frame.Props["group"] = group.ToString(CultureInfo.InvariantCulture);
                frame.Props["index"] = index.ToString(CultureInfo.InvariantCulture);
                frame.Props["x"] = ox.ToString(CultureInfo.InvariantCulture);
                frame.Props["y"] = oy.ToString(CultureInfo.InvariantCulture);
                frame.Props["ticks"] = ticks.ToString(CultureInfo.InvariantCulture);
                frame.Props["startTick"] = tick.ToString(CultureInfo.InvariantCulture);
                if (parts.Length > 5 && parts[5].Length > 0) frame.Props["flip"] = parts[5].ToUpperInvariant();
                if (parts.Length > 6 && parts[6].Length > 0) frame.Props["trans"] = parts[6].ToUpperInvariant();
                b.Relate(RelationKind.Contains, animId, frameId, "structure.contains", line);

                var spriteId = $"sprite:{group},{index}";
                var sprite = b.Add(ObjectKind.Sprite, spriteId, $"Sprite {group},{index}");
                if (sprites is not null)
                {
                    if (sprites.TryGetValue((group, index), out var facts))
                    {
                        sprite.Props["width"] = facts.Width.ToString(CultureInfo.InvariantCulture);
                        sprite.Props["height"] = facts.Height.ToString(CultureInfo.InvariantCulture);
                        sprite.Props["axisX"] = facts.AxisX.ToString(CultureInfo.InvariantCulture);
                        sprite.Props["axisY"] = facts.AxisY.ToString(CultureInfo.InvariantCulture);
                        b.Relate(RelationKind.AnimUsesSprite, frameId, spriteId, "anim.sprite-ref", line);
                    }
                    else
                    {
                        sprite.Props.TryAdd("unresolved", "not in the SFF");
                        b.Relate(RelationKind.AnimUsesSprite, frameId, spriteId, "anim.sprite-missing", line, note: "sprite not in the SFF");
                    }
                }
                else
                {
                    b.Relate(RelationKind.AnimUsesSprite, frameId, spriteId, "anim.sprite-ref", line);
                }

                for (var t = 0; t < 2; t++)
                {
                    var boxes = localDeclared[t] ? locals[t] : defaults[t];
                    for (var j = 0; j < boxes.Count; j++)
                    {
                        var clsnId = $"{frameId}/clsn{t + 1}:{j}";
                        var clsn = b.Add(ObjectKind.Clsn, clsnId, $"Clsn{t + 1}[{j}]", line, frameId);
                        clsn.Props["type"] = (t + 1).ToString(CultureInfo.InvariantCulture);
                        clsn.Props["x1"] = boxes[j][0].ToString(CultureInfo.InvariantCulture);
                        clsn.Props["y1"] = boxes[j][1].ToString(CultureInfo.InvariantCulture);
                        clsn.Props["x2"] = boxes[j][2].ToString(CultureInfo.InvariantCulture);
                        clsn.Props["y2"] = boxes[j][3].ToString(CultureInfo.InvariantCulture);
                        clsn.Props["inherited"] = (!localDeclared[t]).ToString().ToLowerInvariant();
                        b.Relate(RelationKind.HasClsn, frameId, clsnId, "anim.clsn", line);
                    }

                    locals[t].Clear();
                    localDeclared[t] = false;
                }

                if (ticks < 0) infinite = true; else tick += ticks;
                frameIndex++;
            }

            anim.Props["frames"] = frameIndex.ToString(CultureInfo.InvariantCulture);
            anim.Props["totalTicks"] = tick.ToString(CultureInfo.InvariantCulture);
            if (infinite) anim.Props["endsInfinite"] = "true";
            if (loopStart is not null) anim.Props["loopStart"] = loopStart.Value.ToString(CultureInfo.InvariantCulture);
            if (frameIndex == 0) b.Warn("air.empty-action", $"Action {number} has no frames.", span);
        }
    }

    private static int[]? Numbers(string value)
    {
        var parts = value.Split(',').Select(p => p.Trim()).ToArray();
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) result[i] = n;
            else if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) result[i] = (int)Math.Round(d);
            else return null;
        }

        return result;
    }
}
