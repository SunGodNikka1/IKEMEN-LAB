using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Playback;

public sealed record SourceSpan(string File, int StartLine, int EndLine);
public sealed record SnapshotFile(string Path, string Role, string ContentHash);
public sealed record SnapshotTrigger(string Text, SourceSpan? Source);
public sealed record SnapshotGroup(int Number, bool IsExpectedBranch, IReadOnlyList<SnapshotTrigger> Lines);

/// <summary>
/// A controller that the static graph shows reading one of the same commands from the same source. It is listed for context only: nothing here says
/// which controller the engine executed, and file order is not engine priority.
/// </summary>
public sealed record CompetingController(
    string ControllerId, string StateId, string TargetId, string Kind, int BranchNumber, string Confidence, IReadOnlyList<string> SharedCommands,
    int UnmodelledCount, SourceSpan? Source, string FileOrder);

public sealed record StepStaticSnapshot(
    int Index, string EdgeId, string Kind, string From, string To, string ControllerId, string? ControllerName, int BranchNumber, string Confidence,
    IReadOnlyList<string> EvidenceRules, IReadOnlyList<string> Commands, string? Contact, int? EarliestTick,
    IReadOnlyList<string> ModelledRequirements, IReadOnlyList<string> Unmodelled, SourceSpan? ControllerSource,
    IReadOnlyList<SnapshotTrigger> TriggerAll, IReadOnlyList<SnapshotGroup> TriggerGroups,
    IReadOnlyList<CompetingController> CompetingControllers, bool CompetingControllersTruncated);

/// <summary>
/// The static evidence behind a route, captured when a Play attempt STARTS from the immutable graph the attempt was given. A diagnostic built later reads this
/// snapshot, never the live character files, so editing or re-indexing the character cannot change what an old attempt's diagnostic says.
/// </summary>
public sealed record StaticSnapshot(
    string CharacterId, string Schema, IReadOnlyList<SnapshotFile> Files, string RouteKey, IReadOnlyList<StepStaticSnapshot> Steps, IReadOnlyList<string> Limitations)
{
    public const string SchemaVersion = "ikemenlab.xray.static-snapshot/1";
    public const int MaxCompeting = 10;

    public static readonly IReadOnlyList<string> StandardLimitations =
    [
        "Competing controllers are the ones whose gate names a shared command and that start from the same source in the candidate graph. Controllers outside the graph (dynamic targets, helpers, files the index does not read such as ZSS, engine common code) are not covered.",
        "Listing order is file order inside the Statedef. It is not shown to be engine execution priority, and the trace does not record which controller executed (executedController = unknown).",
        "Conditions the index could not model are listed as written. A requirement missing from the modelled list means the index did not model one; it does not mean the engine has none.",
        "Observing a destination state does not establish which controller produced it."
    ];

    public static StaticSnapshot Capture(CandidateGraph graph, ComboRoute route)
    {
        var index = graph.Index;
        var files = index.Files.Select(f => new SnapshotFile(f.RelPath, f.Role.ToString(), f.Hash)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var steps = new List<StepStaticSnapshot>();
        for (var i = 0; i < route.Steps.Count; i++)
            steps.Add(CaptureStep(graph, index, route.Steps[i].Edge, i + 1));
        return new StaticSnapshot(index.CharacterId, SchemaVersion, files, route.Key, steps, StandardLimitations);
    }

    private static StepStaticSnapshot CaptureStep(CandidateGraph graph, SemanticIndex index, CandidateEdge edge, int number)
    {
        SourceSpan? Span(SourceRef? r) => r is { } s && index.FileOf(s) is { } f ? new SourceSpan(f.RelPath, s.StartLine, s.EndLine) : null;
        SnapshotTrigger Trig(TriggerLine l) => new(l.Text, Span(l.Source));

        var controller = index.Get(edge.ControllerId);
        var gate = controller?.Gate;
        var groups = gate?.Branches.Select(b => new SnapshotGroup(b.Number, b.Number == edge.BranchNumber, b.Lines.Select(Trig).ToList())).ToList() ?? [];

        var competing = new List<CompetingController>();
        var total = 0;
        foreach (var other in graph.Edges.Where(e => e.ControllerId != edge.ControllerId && e.From == edge.From && e.Commands.Intersect(edge.Commands).Any())
                     .GroupBy(e => e.ControllerId).Select(g => g.First()).OrderBy(e => Order(e.ControllerId), Comparer<(string, int)>.Default))
        {
            total++;
            if (competing.Count >= MaxCompeting) continue;
            var c = index.Get(other.ControllerId);
            competing.Add(new CompetingController(other.ControllerId, other.ControllerId.Split('/')[0], other.To, other.Kind.ToString(), other.BranchNumber, other.Confidence.ToString(),
                other.Commands.Intersect(edge.Commands).OrderBy(x => x, StringComparer.Ordinal).ToList(), other.Unmodelled.Count, Span(c?.Source),
                FileOrder(edge.ControllerId, other.ControllerId)));
        }

        return new StepStaticSnapshot(number, edge.Id, edge.Kind.ToString(), edge.From, edge.To, edge.ControllerId, controller?.Name, edge.BranchNumber, edge.Confidence.ToString(),
            edge.Evidence.Select(e => e.RuleId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(), edge.Commands.ToList(), edge.Contact == ContactRequirement.None ? null : edge.Contact.ToString().ToLowerInvariant(),
            edge.EarliestTick, Requirements(edge.Facets), edge.Unmodelled.ToList(), Span(controller?.Source),
            gate?.TriggerAll.Select(Trig).ToList() ?? [], groups, competing, total > competing.Count);
    }

    private static (string, int) Order(string controllerId)
    {
        var slash = controllerId.IndexOf("/ctrl:", StringComparison.Ordinal);
        var n = slash >= 0 && int.TryParse(controllerId[(slash + 6)..], out var v) ? v : int.MaxValue;
        return (slash >= 0 ? controllerId[..slash] : controllerId, n);
    }

    private static string FileOrder(string expected, string other)
    {
        var (es, en) = Order(expected);
        var (os, on) = Order(other);
        if (es != os) return $"in {os}, a different Statedef from the expected controller's {es}";
        return on < en ? "earlier in the same Statedef" : "later in the same Statedef";
    }

    /// <summary>The facets the index modelled for the expected branch, as short statements. Not a complete statement of the engine's conditions.</summary>
    public static IReadOnlyList<string> Requirements(GateFacets f)
    {
        var r = new List<string>();
        r.AddRange(f.Commands.Select(c => $"command = \"{c}\""));
        r.AddRange(f.NegatedCommands.Select(c => $"command != \"{c}\""));
        r.AddRange(f.Contact.Select(c => $"contact: {c}"));
        r.AddRange(f.Power.Select(p => $"power {p.Op} {p.Value:0.##}"));
        r.AddRange(f.Time.Select(p => $"time {p.Op} {p.Value:0.##}"));
        r.AddRange(f.AnimElem.Select(p => $"animelem {p.Op} {p.Value:0.##}"));
        if (f.CtrlRequired) r.Add("ctrl");
        r.AddRange(f.StateTypes.Select(s => $"statetype = {s}"));
        r.AddRange(f.MoveTypes.Select(s => $"movetype = {s}"));
        if (f.SourceStateRanges.Count > 0) r.Add("source state in " + string.Join(", ", f.SourceStateRanges));
        if (f.ExcludedStateRanges.Count > 0) r.Add("source state not in " + string.Join(", ", f.ExcludedStateRanges));
        if (f.PrevStateRanges.Count > 0) r.Add("previous state in " + string.Join(", ", f.PrevStateRanges));
        if (f.ReadsAiLevel) r.Add("reads AILevel");
        return r;
    }
}
