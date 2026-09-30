using System.Globalization;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Query;

namespace IKEMENLab.Core.XRay.Combo;

/// <summary>
/// Deterministic search over the candidate graph. Every route it returns is a <em>candidate</em>: each step is an edge the files
/// allow, with the weakest edge confidence and every unmodelled condition carried along. It does not simulate hit-stun, pushback,
/// juggle points, scaling or meter gain, so no route is ever called valid or verified.
/// </summary>
public static class ComboSearch
{
    private sealed class Partial
    {
        public Partial? Parent;
        public CandidateEdge? Edge;
        public required MoveInfo Move;
        public string NodeId = string.Empty;
        public int Meter;
        public int MoveCount;
        public int Depth;
        public double Damage;
        public bool DamageComplete = true;
        public int MeterSpent;
        public int FramesLowerBound;
        public bool FramesComplete = true;
        public bool AnyHit;

        public int CountOf(string stateId)
        {
            var n = 0;
            for (var p = this; p is not null; p = p.Parent) if (p.Move.StateId == stateId) n++;
            return n;
        }
    }

    private static readonly MoveInfo NeutralMove = new(CandidateGraph.NeutralId, -100, "Neutral", null, "I", false, true, 0, null, false, null, null, null, null, null, null, null, 0, []);

    public static ComboSearchResult Find(CandidateGraph graph, ComboOptions options)
    {
        var warnings = new List<string>();
        var startId = CandidateGraph.NeutralId;
        Partial root;
        if (options.From is not null)
        {
            var resolved = CandidateGraph.ResolveState(graph.Index, options.From);
            if (resolved is null || graph.Move(resolved) is not { } startMove)
            {
                return new ComboSearchResult { Options = options, Routes = [], Expansions = 0, Truncated = false, Warnings = [$"'{options.From}' is not a defined state."] };
            }

            startId = startMove.StateId;
            root = new Partial { Move = startMove, NodeId = startId, Meter = options.StartMeter, Damage = startMove.Damage ?? 0, DamageComplete = startMove.HitDefCount == 0 || startMove.DamageExact, AnyHit = startMove.HitDefCount > 0 };
            if (startMove.HitDefCount > 0 && startMove.Damage is null) root.DamageComplete = false;
        }
        else
        {
            root = new Partial { Move = NeutralMove, NodeId = CandidateGraph.NeutralId, Meter = options.StartMeter };
        }

        var found = new Dictionary<string, ComboRoute>(StringComparer.Ordinal);
        var expansions = 0;
        var truncated = false;

        void Record(Partial p)
        {
            if (p.Depth == 0 || !p.AnyHit) return;
            var route = ToRoute(p, startId);
            found.TryAdd(route.Key, route);
        }

        switch (options.Strategy)
        {
            case ComboStrategy.Dfs:
                Dfs(graph, options, root, Record, ref expansions, ref truncated);
                break;
            case ComboStrategy.Beam:
                Beam(graph, options, root, Record, ref expansions, ref truncated);
                break;
            default:
                BestFirst(graph, options, root, Record, ref expansions, ref truncated);
                break;
        }

        if (graph.From(startId).Count == 0) warnings.Add(options.From is null ? "No command starts a move from neutral in the candidate graph." : $"{startId} has no outgoing candidate edges.");
        if (truncated) warnings.Add($"The search stopped after {options.MaxExpansions:N0} expansions; the list is the best found so far, not everything.");
        warnings.Add("Routes are candidates. Hit-stun, pushback, juggle points, damage scaling, meter gain and cancel timing are not modelled.");

        var routes = found.Values.OrderByDescending(r => r.DamageKnown).ThenBy(r => r.UnmodelledCount).ThenBy(r => Rank(r.Confidence))
            .ThenByDescending(r => r.Steps.Count).ThenBy(r => r.Key, StringComparer.Ordinal).Take(options.Top).ToList();
        return new ComboSearchResult { Options = options, Routes = routes, Expansions = expansions, Truncated = truncated, Warnings = warnings };
    }

    // ------------------------------------------------------------------ strategies

    private static void Dfs(CandidateGraph g, ComboOptions o, Partial root, Action<Partial> record, ref int expansions, ref bool truncated)
    {
        var stack = new Stack<Partial>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            record(p);
            if (expansions >= o.MaxExpansions) { truncated = true; break; }
            expansions++;
            var children = Children(g, o, p);
            for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);   // reversed so edges are visited in id order
        }
    }

    private static void Beam(CandidateGraph g, ComboOptions o, Partial root, Action<Partial> record, ref int expansions, ref bool truncated)
    {
        var level = new List<Partial> { root };
        while (level.Count > 0)
        {
            var next = new List<Partial>();
            foreach (var p in level)
            {
                if (expansions >= o.MaxExpansions) { truncated = true; return; }
                expansions++;
                foreach (var c in Children(g, o, p)) { record(c); next.Add(c); }
            }

            level = next.OrderByDescending(p => p.Damage).ThenBy(p => Unmodelled(p)).ThenBy(p => Key(p), StringComparer.Ordinal).Take(o.BeamWidth).ToList();
        }
    }

    private static void BestFirst(CandidateGraph g, ComboOptions o, Partial root, Action<Partial> record, ref int expansions, ref bool truncated)
    {
        var queue = new PriorityQueue<Partial, (double NegDamage, int Unmodelled, string Key)>();
        queue.Enqueue(root, (0, 0, string.Empty));
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            record(p);
            if (expansions >= o.MaxExpansions) { truncated = true; break; }
            expansions++;
            foreach (var c in Children(g, o, p)) queue.Enqueue(c, (-c.Damage, Unmodelled(c), Key(c)));
        }
    }

    // ------------------------------------------------------------------ expansion

    private static List<Partial> Children(CandidateGraph g, ComboOptions o, Partial p)
    {
        var result = new List<Partial>();
        foreach (var e in g.From(p.NodeId).OrderBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.ControllerId, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            if (!Allowed(e, o)) continue;

            var toNeutral = e.To == CandidateGraph.NeutralId;
            var move = toNeutral ? NeutralMove : g.Move(e.To);
            if (move is null) continue;

            var startsMove = e.Kind is EdgeKind.Start or EdgeKind.Cancel or EdgeKind.Link;
            var moveCount = p.MoveCount + (startsMove ? 1 : 0);
            if (moveCount > o.MaxMoves) continue;
            if (!toNeutral && p.CountOf(move.StateId) >= o.MaxRepeats) continue;
            if (p.Depth >= (o.MaxMoves * 3) + 3) continue;

            // Meter: a gate needs at least its threshold, and a move needs to afford its cost.
            var meter = p.Meter;
            var need = 0;
            foreach (var pg in e.Facets.Power)
            {
                if (pg.Op == ">=") need = Math.Max(need, (int)Math.Ceiling(pg.Value));
                else if (pg.Op == ">") need = Math.Max(need, (int)Math.Floor(pg.Value) + 1);
            }

            var cost = (int)Math.Ceiling(move.PowerCost);
            if (meter < need || meter < cost) continue;

            var child = new Partial
            {
                Parent = p, Edge = e, Move = move, NodeId = e.To, Meter = meter - cost, MoveCount = moveCount, Depth = p.Depth + 1,
                MeterSpent = p.MeterSpent + cost, AnyHit = p.AnyHit || move.HitDefCount > 0,
                Damage = p.Damage, DamageComplete = p.DamageComplete, FramesLowerBound = p.FramesLowerBound, FramesComplete = p.FramesComplete
            };

            if (!toNeutral && move.HitDefCount > 0)
            {
                if (move.Damage is { } d) child.Damage += d; else child.DamageComplete = false;
                if (!move.DamageExact) child.DamageComplete = false;
            }

            // Frames: the tick of the source state at which this edge can first fire, when the gate says so.
            if (p.NodeId != CandidateGraph.NeutralId)
            {
                if (e.EarliestTick is { } tick) child.FramesLowerBound += tick; else child.FramesComplete = false;
            }

            if (o.MaxFrames is { } limit && FramesWithTail(child) > limit) continue;
            result.Add(child);
        }

        return result;
    }

    private static int FramesWithTail(Partial p) => p.FramesLowerBound + (p.Move.FirstHitTick ?? 0);

    private static bool Allowed(CandidateEdge e, ComboOptions o)
    {
        if (Rank(e.Confidence) > Rank(o.WeakestAllowed)) return false;
        if (o.MaxUnmodelledPerEdge is { } max && e.Unmodelled.Count > max) return false;
        switch (e.Kind)
        {
            case EdgeKind.Recovery or EdgeKind.Link: return o.AllowLinks;
            case EdgeKind.Chain: if (!o.AllowChains) return false; break;
            case EdgeKind.Cancel:
                if (o.HitConfirmOnly && !e.IsHitConfirm) return false;
                if (e.Contact != ContactRequirement.None && !e.SourceHasHitDef) return false;   // contact on a move with no HitDef can never hold
                break;
        }

        return true;
    }

    // ------------------------------------------------------------------ results

    private static ComboRoute ToRoute(Partial leaf, string startId)
    {
        var chain = new List<Partial>();
        for (var p = leaf; p is not null && p.Edge is not null; p = p.Parent) chain.Add(p);
        chain.Reverse();

        var steps = new List<ComboStep>();
        var notes = new List<string> { EvidenceRules.Get("combo.route").Description };
        foreach (var p in chain)
        {
            double? d = p.Move.HitDefCount == 0 ? null : p.Move.Damage;
            steps.Add(new ComboStep(p.Edge!, p.Move, d, p.Meter));
        }

        var confidence = chain.Select(p => p.Edge!.Confidence).Aggregate(Confidence.StaticProven, StateGraph.Weakest);
        if (chain.Any(p => p.Edge!.Kind == EdgeKind.Link)) notes.Add("Contains a link: it needs control back first, and the timing is not modelled.");
        if (chain.Any(p => p.Edge!.Kind == EdgeKind.Recovery)) notes.Add("Passes through neutral: this is a sequence of separate moves, not a cancel string.");
        if (!leaf.DamageComplete) notes.Add("Damage is a lower bound: a move has several HitDefs or a non-literal damage.");
        if (chain.Any(p => p.Edge!.Facets.Power.Count > 0)) notes.Add("Meter gain from hits is not modelled; only the starting meter and literal costs are.");
        if (!leaf.FramesComplete) notes.Add("Frame count is a lower bound: some cancel windows have no Time/AnimElem gate.");

        return new ComboRoute
        {
            StartState = startId,
            Steps = steps,
            DamageKnown = leaf.Damage,
            DamageComplete = leaf.DamageComplete,
            MeterSpent = leaf.MeterSpent,
            EndMeter = leaf.Meter,
            MinFrames = leaf.FramesLowerBound + (leaf.Move.FirstHitTick ?? 0),
            FramesComplete = leaf.FramesComplete && (leaf.Move.HitDefCount == 0 || leaf.Move.FirstHitTick is not null),
            Confidence = confidence,
            UnmodelledCount = chain.Sum(p => p.Edge!.Unmodelled.Count),
            Notes = notes
        };
    }

    private static int Unmodelled(Partial p)
    {
        var n = 0;
        for (var q = p; q?.Edge is not null; q = q.Parent) n += q.Edge.Unmodelled.Count;
        return n;
    }

    private static string Key(Partial p)
    {
        var parts = new List<string>();
        for (var q = p; q?.Edge is not null; q = q.Parent) parts.Add(q.Edge.Id);
        parts.Reverse();
        return string.Join(">", parts);
    }

    private static int Rank(Confidence c) => c switch
    {
        Confidence.RuntimeVerified => -1,
        Confidence.StaticProven => 0,
        Confidence.Inferred => 1,
        _ => 2
    };

    public static string Describe(ComboRoute r) =>
        string.Join(" → ", r.Steps.Select(s => s.Move.StateId.Replace("state:", "State ")))
        + string.Create(CultureInfo.InvariantCulture, $"  ({r.DamageKnown:0.##}{(r.DamageComplete ? string.Empty : "+")} damage, {r.Confidence})");
}
