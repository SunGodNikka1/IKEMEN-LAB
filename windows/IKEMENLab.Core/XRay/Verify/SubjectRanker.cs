using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Verify;

/// <summary>How suitable one character is as the first runtime-verification subject, with the numbers behind the score.</summary>
public sealed record SubjectScore(
    string Character, double Score, int Edges, int CleanEdges, int CleanHitConfirmCancels, int ScriptableRoutes, int LongestScriptableRoute,
    int DynamicTargets, int AiEntryPoints, int LiteralCommandStarts, int LinkEdges, bool HasSuper, IReadOnlyList<string> Reasons, IReadOnlyList<string> Concerns)
{
    public ComboRoute? BestRoute { get; init; }
}

/// <summary>
/// Ranks characters by how well a route can be scripted and judged: many edges with no unmodelled conditions, hit-confirm cancels,
/// literal command starts, few dynamic redirects and little AI-driven control flow. It is a static heuristic for choosing the first
/// subject, not a statement about the character's quality.
/// </summary>
public static class SubjectRanker
{
    public static SubjectScore Score(CandidateGraph graph)
    {
        var edges = graph.Edges;
        static bool Clean(CandidateEdge e) => e.Unmodelled.Count == 0 && e.Confidence == Confidence.StaticProven;
        var clean = edges.Count(Clean);
        var cleanCancels = edges.Count(e => Clean(e) && e.Kind == EdgeKind.Cancel && e.IsHitConfirm);
        var literalStarts = edges.Count(e => e.Kind == EdgeKind.Start && Clean(e) && e.Commands.Count > 0);
        var links = edges.Count(e => e.Kind == EdgeKind.Link);
        var aiEntry = graph.Index.AiEntryPoints().Count();
        var dynamic = graph.DynamicTargets.Count;

        var search = ComboSearch.Find(graph, new ComboOptions { MaxMoves = 4, HitConfirmOnly = true, MaxUnmodelledPerEdge = 0, Top = 60 });
        var scriptable = new List<ComboRoute>();
        foreach (var r in search.Routes)
            if (r.Steps.Count >= 2 && InputPlanner.Plan(graph, r).Plan is not null) scriptable.Add(r);

        var hasSuper = graph.Moves.Any(m => m.PowerCost >= 1000);
        var best = scriptable.OrderByDescending(r => r.Steps.Count).ThenByDescending(r => r.DamageKnown).FirstOrDefault();
        var longest = best?.Steps.Count ?? 0;

        var cleanFraction = edges.Count == 0 ? 0 : (double)clean / edges.Count;
        var dynamicPenalty = edges.Count == 0 ? 0 : Math.Min(1.0, (double)dynamic / Math.Max(1, graph.Moves.Count));
        var score = 100 * cleanFraction
                    + Math.Min(40, cleanCancels * 4)
                    + Math.Min(40, scriptable.Count * 4)
                    + longest * 5
                    + (literalStarts > 0 ? 10 : 0)
                    + (links > 0 ? 5 : 0)
                    + (hasSuper ? 5 : 0)
                    - 60 * dynamicPenalty
                    - Math.Min(30, aiEntry * 3);

        var reasons = new List<string>();
        var concerns = new List<string>();
        if (scriptable.Count > 0) reasons.Add($"{scriptable.Count} scriptable neutral-start route(s), longest {longest} moves, all edges free of unmodelled conditions");
        else concerns.Add("No route made only of clean edges can be scripted from neutral");
        if (cleanCancels > 0) reasons.Add($"{cleanCancels} clean hit-confirm cancel edge(s)");
        if (literalStarts > 0) reasons.Add($"{literalStarts} literal-command move start(s)");
        if (links > 0) reasons.Add($"{links} link edge(s) present (links are searched only with --allow-links)");
        if (hasSuper) reasons.Add("has a move costing 1000+ power");
        if (dynamic > 0) concerns.Add($"{dynamic} dynamic transition target(s)");
        if (aiEntry > 0) concerns.Add($"{aiEntry} AI-gated controller(s)");
        if (cleanFraction < 0.5) concerns.Add($"only {cleanFraction:P0} of edges are free of unmodelled conditions");

        return new SubjectScore(graph.Index.CharacterId, Math.Round(score, 2), edges.Count, clean, cleanCancels, scriptable.Count, longest,
            dynamic, aiEntry, literalStarts, links, hasSuper, reasons, concerns) { BestRoute = best };
    }
}
