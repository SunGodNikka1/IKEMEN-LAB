using System.Globalization;
using System.Text.RegularExpressions;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Indexing;

internal sealed class StateData
{
    public required string Id { get; init; }
    public required int Number { get; init; }
    public required SemanticObject Obj { get; init; }
    public required bool IsCommon { get; init; }
    public required RawBlock Block { get; init; }
    public required int FileId { get; init; }
    public required int Ordinal { get; init; }
    public List<ControllerData> Controllers { get; } = [];
    public Dictionary<string, RawEntry> Params { get; } = new(StringComparer.Ordinal);
}

internal sealed class ControllerData
{
    public required string Id { get; init; }
    public required StateData State { get; init; }
    public required SemanticObject Obj { get; init; }
    public required string Type { get; init; }
    public required RawBlock Block { get; init; }
    public required int FileId { get; init; }
    public required Gate Gate { get; init; }
    /// <summary>All non-trigger parameters, lower-cased keys, last one wins.</summary>
    public Dictionary<string, RawEntry> Params { get; } = new(StringComparer.Ordinal);
    public List<RawEntry> ParamList { get; } = [];
    public SourceRef Span => Block.Span(FileId);
}

internal sealed class StateIndexResult
{
    public List<StateData> States { get; } = [];
    public Dictionary<int, StateData> Effective { get; } = [];
    public IEnumerable<ControllerData> Controllers => States.SelectMany(s => s.Controllers);
}

/// <summary>Phase 1: [Statedef]/[State] blocks → state and controller objects with gates. Relationships come later (<see cref="StateLinker"/>).</summary>
public static partial class StateIndexer
{
    [GeneratedRegex(@"^statedef\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StatedefHeader();

    [GeneratedRegex(@"^state\b\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex StateHeader();

    [GeneratedRegex(@"^trigger(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TriggerKey();

    private static readonly HashSet<string> NonParamKeys = new(StringComparer.Ordinal) { "type", "persistent", "ignorehitpause", "name" };

    internal static void Index(IndexBuilder b, SourceFile file, IReadOnlyList<RawBlock> blocks, StateIndexResult result,
        Dictionary<int, int> definitionCounts)
    {
        StateData? current = null;
        var ctrlOrdinal = 0;

        foreach (var block in blocks)
        {
            var span = block.Span(file.Id);
            var sd = StatedefHeader().Match(block.Header);
            if (sd.Success)
            {
                var numberText = sd.Groups[1].Value.Split(',')[0].Trim();
                if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    b.Warn("state.bad-number", $"'[{block.Header}]' does not have a whole-number state id; its controllers were skipped.", span);
                    current = null;
                    continue;
                }

                definitionCounts[number] = definitionCounts.TryGetValue(number, out var seen) ? seen + 1 : 1;
                var ordinal = definitionCounts[number];
                var id = ordinal == 1 ? $"state:{number}" : $"state:{number}#{ordinal}";
                var obj = b.Add(ObjectKind.State, id, $"State {number}", span);
                obj.Props["number"] = number.ToString(CultureInfo.InvariantCulture);
                if (file.IsCommon) obj.Props["common"] = "true";
                if (ordinal > 1) obj.Props["shadowed"] = "true";
                if (block.LeadingComments.Count > 0)
                {
                    obj.Props["comment"] = string.Join(" / ", block.LeadingComments);
                    b.Label(obj, block.LeadingComments[^1], "name", "label.author-comment");
                }

                var data = new StateData
                {
                    Id = id, Number = number, Obj = obj, IsCommon = file.IsCommon, Block = block, FileId = file.Id, Ordinal = ordinal
                };
                foreach (var e in block.Entries.Where(e => e.HadEquals))
                {
                    var key = e.Key.ToLowerInvariant();
                    data.Params[key] = e;
                    obj.Props["p." + key] = e.Value;
                }

                foreach (var e in block.Entries.Where(e => !e.HadEquals))
                    b.Warn("state.stray-line", $"Line '{e.Key}' in [{block.Header}] has no '='.", SourceRef.At(file.Id, e.Line), DiagnosticSeverity.Info);

                if (ordinal > 1 && !file.IsCommon && !result.States.Any(s => s.Number == number && s.IsCommon && s.Ordinal == 1))
                    b.Warn("state.duplicate", $"State {number} is defined more than once; the first definition is treated as the one in effect.", span);

                result.States.Add(data);
                if (!result.Effective.ContainsKey(number)) result.Effective[number] = data;
                current = data;
                ctrlOrdinal = 0;
                continue;
            }

            var st = StateHeader().Match(block.Header);
            if (st.Success)
            {
                if (current is null)
                {
                    b.Warn("state.orphan", $"'[{block.Header}]' appears before any Statedef and was skipped.", span);
                    continue;
                }

                var rest = st.Groups[1].Value;
                var comma = rest.IndexOf(',');
                var name = comma >= 0 ? rest[(comma + 1)..].Trim() : string.Empty;
                var id = $"{current.Id}/ctrl:{ctrlOrdinal++}";

                var type = string.Empty;
                var entries = new List<RawEntry>();
                foreach (var e in block.Entries)
                {
                    if (!e.HadEquals)
                    {
                        b.Warn("state.stray-line", $"Line '{e.Key}' in [{block.Header}] has no '='.", SourceRef.At(file.Id, e.Line), DiagnosticSeverity.Info);
                        continue;
                    }

                    if (e.Key.Equals("type", StringComparison.OrdinalIgnoreCase)) type = e.Value.Trim().ToLowerInvariant();
                    entries.Add(e);
                }

                if (type.Length == 0)
                    b.Warn("state.no-type", $"Controller '[{block.Header}]' has no type=.", span);

                var gate = BuildGate(file.Id, entries, b);
                var obj = b.Add(ObjectKind.Controller, id, name.Length > 0 ? name : (type.Length > 0 ? type : "controller"), span, current.Id);
                obj.Gate = gate;
                obj.Props["type"] = type;
                if (name.Length > 0) obj.Props["name"] = name;
                if (block.LeadingComments.Count > 0) obj.Props["comment"] = string.Join(" / ", block.LeadingComments);

                var ctrl = new ControllerData
                {
                    Id = id, State = current, Obj = obj, Type = type, Block = block, FileId = file.Id, Gate = gate
                };
                foreach (var e in entries)
                {
                    var key = e.Key.ToLowerInvariant();
                    if (key == "triggerall" || TriggerKey().IsMatch(key)) continue;
                    ctrl.Params[key] = e;
                    ctrl.ParamList.Add(e);
                    if (key is "persistent" or "ignorehitpause") obj.Props[key] = e.Value;
                    else if (key != "type") obj.Props["p." + key] = e.Value;
                }

                if (gate.IsEmpty) b.Warn("state.no-trigger", $"Controller '[{block.Header}]' has no triggers and will never fire.", span, DiagnosticSeverity.Info);
                current.Controllers.Add(ctrl);
                b.Relate(RelationKind.Contains, current.Id, id, "structure.contains", span);
                continue;
            }

            // [Command], [Defaults], [Info], [Files]… are handled by other indexers or are not behaviour.
        }
    }

    private static Gate BuildGate(int fileId, List<RawEntry> entries, IndexBuilder b)
    {
        var all = new List<TriggerLine>();
        var groups = new SortedDictionary<int, List<TriggerLine>>();
        foreach (var e in entries)
        {
            var key = e.Key.ToLowerInvariant();
            var isAll = key == "triggerall";
            var m = TriggerKey().Match(key);
            if (!isAll && !m.Success) continue;

            var expr = ExprParser.Parse(e.Value);
            var line = new TriggerLine(e.Value, expr, SourceRef.At(fileId, e.Line));
            if (expr is RawExpr raw)
                b.Warn("expr.unparsed", $"Cannot parse '{e.Value}' ({raw.Error}); treated as an unknown condition.", line.Source);
            if (isAll) all.Add(line);
            else
            {
                var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                (groups.TryGetValue(n, out var list) ? list : groups[n] = []).Add(line);
            }
        }

        var branches = new List<GateBranch>();
        foreach (var (number, lines) in groups)
        {
            foreach (var facets in GateAnalyzer.Expand(all, lines))
                branches.Add(new GateBranch(number, lines, facets));
        }

        return new Gate(all, branches);
    }
}
