using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Query;

public enum StateEdgeKind { Change, Victim, Attacker, HelperSpawn }

/// <summary>A state-to-state edge aggregated from the controllers that create it. One edge per (from, to, kind, confidence).</summary>
public sealed record StateEdge(string From, string To, StateEdgeKind Kind, Confidence Confidence, IReadOnlyList<string> ControllerIds, string? ViaHelper);

/// <summary>The index seen as a directed graph of states, the base for the State Graph lens and for combo candidates in milestone 2.</summary>
public sealed class StateGraph
{
    private readonly Dictionary<string, List<StateEdge>> _out = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<StateEdge>> _in = new(StringComparer.Ordinal);

    public IReadOnlyList<StateEdge> Edges { get; }

    private StateGraph(List<StateEdge> edges)
    {
        Edges = edges;
        foreach (var e in edges)
        {
            (_out.TryGetValue(e.From, out var o) ? o : _out[e.From] = []).Add(e);
            (_in.TryGetValue(e.To, out var i) ? i : _in[e.To] = []).Add(e);
        }
    }

    public IReadOnlyList<StateEdge> Outgoing(string id) => _out.TryGetValue(id, out var l) ? l : [];
    public IReadOnlyList<StateEdge> Incoming(string id) => _in.TryGetValue(id, out var l) ? l : [];

    public static StateGraph Build(SemanticIndex index)
    {
        var raw = new List<(string From, string To, StateEdgeKind Kind, Confidence Conf, string Ctrl, string? Helper)>();
        foreach (var r in index.Relationships)
        {
            switch (r.Kind)
            {
                case RelationKind.ChangesState or RelationKind.SetsVictimState or RelationKind.SetsAttackerState:
                {
                    var owner = index.OwnerState(r.From);
                    if (owner is null) break;
                    var ctrl = r.From.EndsWith("/hitdef", StringComparison.Ordinal) ? r.From[..^7] : r.From;
                    var kind = r.Kind == RelationKind.ChangesState ? StateEdgeKind.Change
                        : r.Kind == RelationKind.SetsVictimState ? StateEdgeKind.Victim : StateEdgeKind.Attacker;
                    raw.Add((owner.Id, r.To, kind, r.Confidence, ctrl, null));
                    break;
                }
                case RelationKind.SpawnsHelper:
                {
                    var owner = index.OwnerState(r.From);
                    if (owner is null) break;
                    foreach (var run in index.Outgoing(r.To, RelationKind.HelperRunsState))
                        raw.Add((owner.Id, run.To, StateEdgeKind.HelperSpawn, Weakest(r.Confidence, run.Confidence), r.From, r.To));
                    break;
                }
            }
        }

        var edges = raw
            .GroupBy(e => (e.From, e.To, e.Kind, e.Conf, e.Helper))
            .Select(g => new StateEdge(g.Key.From, g.Key.To, g.Key.Kind, g.Key.Conf, g.Select(x => x.Ctrl).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(), g.Key.Helper))
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.Kind).ThenBy(e => e.Confidence)
            .ToList();
        return new StateGraph(edges);
    }

    public static Confidence Weakest(Confidence a, Confidence b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(Confidence c) => c switch
    {
        Confidence.RuntimeVerified => -1,
        Confidence.StaticProven => 0,
        Confidence.Inferred => 1,
        _ => 2
    };
}

public sealed record LayoutNode(string Id, int Layer, double X, double Y, bool IsCenter);

public sealed record GraphLayoutResult(IReadOnlyList<LayoutNode> Nodes, IReadOnlyList<StateEdge> Edges, double Width, double Height, bool Truncated);

/// <summary>Deterministic layered layout of a state's neighbourhood: incoming states on the left, outgoing on the right.</summary>
public static class GraphLayout
{
    public const double ColumnWidth = 210;
    public const double RowHeight = 64;
    public const int MaxNodes = 90;

    public static GraphLayoutResult Neighborhood(StateGraph graph, string center, int depth)
    {
        depth = Math.Clamp(depth, 1, 4);
        var layer = new Dictionary<string, int>(StringComparer.Ordinal) { [center] = 0 };
        var truncated = false;

        void Walk(bool forward)
        {
            var frontier = new List<string> { center };
            for (var d = 1; d <= depth && frontier.Count > 0; d++)
            {
                var next = new List<string>();
                foreach (var id in frontier.OrderBy(x => x, StringComparer.Ordinal))
                {
                    foreach (var e in forward ? graph.Outgoing(id) : graph.Incoming(id))
                    {
                        var other = forward ? e.To : e.From;
                        if (layer.ContainsKey(other)) continue;
                        if (layer.Count >= MaxNodes) { truncated = true; continue; }
                        layer[other] = forward ? d : -d;
                        next.Add(other);
                    }
                }

                frontier = next;
            }
        }

        Walk(forward: true);
        Walk(forward: false);

        var nodes = new List<LayoutNode>();
        foreach (var group in layer.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
        {
            var ids = group.Select(kv => kv.Key).OrderBy(x => StateSort(x), Comparer<(int, string)>.Default).ToList();
            for (var i = 0; i < ids.Count; i++)
                nodes.Add(new LayoutNode(ids[i], group.Key, (group.Key + depth) * ColumnWidth, (i - (ids.Count - 1) / 2.0) * RowHeight, ids[i] == center));
        }

        var minY = nodes.Count == 0 ? 0 : nodes.Min(n => n.Y);
        nodes = nodes.Select(n => n with { Y = n.Y - minY }).ToList();
        var edges = graph.Edges.Where(e => layer.ContainsKey(e.From) && layer.ContainsKey(e.To)).ToList();
        return new GraphLayoutResult(nodes, edges, nodes.Count == 0 ? 0 : nodes.Max(n => n.X) + ColumnWidth, nodes.Count == 0 ? 0 : nodes.Max(n => n.Y) + RowHeight, truncated);
    }

    private static (int, string) StateSort(string id)
    {
        // "state:200" before "state:1500"; stubs and dynamic nodes last.
        if (id.StartsWith("state:", StringComparison.Ordinal) && int.TryParse(id.AsSpan(6).ToString().Split('#', '/')[0], out var n))
            return (n, id);
        return (int.MaxValue, id);
    }
}
