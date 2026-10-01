using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.App.ViewModels;

/// <summary>
/// Candidate combo edges out of the selected state, and a route finder over the same candidate graph. Everything shown is a
/// candidate: rows carry the confidence of the facts they depend on, list what is not modelled, and never say "verified".
/// </summary>
public sealed class ComboLens : XRayLens
{
    private CandidateGraph? _graph;
    private string? _center;
    private XRayRow? _selectedEdge;
    private XRayRow? _selectedRoute;
    private XRayRow? _selectedStep;
    private bool _busy;
    private int _maxMoves = 4;
    private string _meterText = "0";
    private bool _hitConfirmOnly = true;
    private bool _allowLinks;
    private bool _fromSelected;
    private string _caption = "Select a state";
    private string _routeStatus = "Choose options and press Find routes.";
    private string _readiness = string.Empty;
    private IReadOnlyList<ComboRoute> _routes = [];
    private VerificationReport? _report;

    public ComboLens(XRayViewModel owner) : base(owner)
    {
        FindRoutesCommand = new AsyncRelayCommand(FindAsync, () => _graph is not null && !_busy);
        Playback = new ComboPlaybackPanel(owner, this);
    }

    /// <summary>Play / Replay / Inspect Failure / View Trace for the selected route.</summary>
    public ComboPlaybackPanel Playback { get; }
    public CandidateGraph? Graph => _graph;

    /// <summary>The selected route and its display title, or null when none is selected.</summary>
    public (ComboRoute Route, string Title)? CurrentRoute =>
        _selectedRoute is { } row && _routes.FirstOrDefault(r => r.Key == row.Id) is { } route ? (route, row.Title) : null;

    /// <summary>Shows a finished run's verdict on the steps of the route it belongs to (✓ observed, ✗ failed here, · not reached).</summary>
    public void ApplyVerdicts(VerificationReport? report)
    {
        _report = report;
        ShowSteps(CurrentRoute?.Route);
    }

    /// <summary>Selects a step row (1-based) so the failing step is highlighted and shown in every lens.</summary>
    public void SelectStep(int index, string? fromStateId, string? toStateId) =>
        SelectedStep = index >= 1 && index <= RouteSteps.Count ? RouteSteps[index - 1] : SelectedStep;

    public ObservableCollection<XRayRow> Edges { get; } = [];
    public ObservableCollection<XRayRow> Routes { get; } = [];
    public ObservableCollection<XRayRow> RouteSteps { get; } = [];
    public ICommand FindRoutesCommand { get; }

    public string Caption { get => _caption; private set => SetProperty(ref _caption, value); }
    public string RouteStatus { get => _routeStatus; private set => SetProperty(ref _routeStatus, value); }
    public string Readiness { get => _readiness; private set => SetProperty(ref _readiness, value); }

    public double MaxMoves
    {
        get => _maxMoves;
        set
        {
            var v = (int)Math.Clamp(Math.Round(value), 1, 6);
            if (SetProperty(ref _maxMoves, v)) OnPropertyChanged(nameof(MaxMovesText));
        }
    }

    public string MaxMovesText => _maxMoves == 1 ? "1 move" : $"{_maxMoves} moves";
    public string MeterText { get => _meterText; set => SetProperty(ref _meterText, value); }
    public bool HitConfirmOnly { get => _hitConfirmOnly; set => SetProperty(ref _hitConfirmOnly, value); }
    public bool AllowLinks { get => _allowLinks; set => SetProperty(ref _allowLinks, value); }
    public bool FromSelected { get => _fromSelected; set => SetProperty(ref _fromSelected, value); }

    public XRayRow? SelectedEdge
    {
        get => _selectedEdge;
        set
        {
            if (SetProperty(ref _selectedEdge, value) && value is not null && value.Id.StartsWith("state:", StringComparison.Ordinal))
                Owner.Select(value.Id);
        }
    }

    public XRayRow? SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (!SetProperty(ref _selectedRoute, value)) return;
            ShowSteps(value is null ? null : _routes.FirstOrDefault(r => r.Key == value.Id));
            Playback.OnRouteChanged();
        }
    }

    public XRayRow? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (SetProperty(ref _selectedStep, value) && value is not null && value.Id.StartsWith("state:", StringComparison.Ordinal))
                Owner.Select(value.Id);
        }
    }

    // ------------------------------------------------------------------ lens contract

    public override void Build(SemanticIndex index)
    {
        _graph = CandidateGraph.Build(index);
        var r = _graph.Readiness();
        Readiness = $"{r.Edges} candidate edges over {r.MoveStates} move states · {r.EdgesWithUnmodelled} with unmodelled conditions · {r.DynamicTargets} dynamic targets not visible. " +
                    (r.Findings.Count > 0 ? r.Findings[0] : string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related)
    {
        if (_graph is null || id is null) return;
        foreach (var row in Edges) row.IsHighlighted = related.Contains(row.Id);
        foreach (var row in RouteSteps) row.IsHighlighted = related.Contains(row.Id);

        var center = CenterFor(index, id);
        if (center is null || center == _center) return;
        _center = center;
        ShowEdges(index);
    }

    private static string? CenterFor(SemanticIndex index, string id)
    {
        var obj = index.Get(id);
        if (obj is null) return null;
        return obj.Kind switch
        {
            ObjectKind.State when !obj.IsStub => id,
            ObjectKind.Controller or ObjectKind.HitDef => index.OwnerState(id)?.Id,
            ObjectKind.Ability => obj.Prop("entryState"),
            _ => null
        };
    }

    // ------------------------------------------------------------------ edges

    private void ShowEdges(SemanticIndex index)
    {
        Edges.Clear();
        var move = _center is null ? null : _graph!.Move(_center);
        var node = move is { IsNeutral: true } ? CandidateGraph.NeutralId : _center;
        var edges = node is null ? [] : _graph!.From(node);

        Caption = move is null
            ? "Select a state"
            : $"{move.Name}{(move.IsNeutral ? " (neutral)" : string.Empty)} — {edges.Count} candidate edge(s) out of it" +
              (move.Damage is { } d ? $" · {d:0.##}{(move.DamageExact ? string.Empty : "+")} damage" : string.Empty) +
              (move.PowerCost > 0 ? $" · costs {move.PowerCost:0} meter" : string.Empty);

        foreach (var e in edges.Take(300))
        {
            var target = e.To == CandidateGraph.NeutralId ? "neutral" : _graph!.Move(e.To)?.Name ?? e.To;
            Edges.Add(new XRayRow(e.To == CandidateGraph.NeutralId ? e.Id : e.To, $"{KindText(e.Kind)} → {target}", Describe(e), e.Confidence,
                tooltip: Tooltip(e)));
        }
    }

    private static string KindText(EdgeKind k) => k switch
    {
        EdgeKind.Start => "Start",
        EdgeKind.Cancel => "Cancel",
        EdgeKind.OnHit => "On hit",
        EdgeKind.Chain => "Continues",
        EdgeKind.Link => "Link",
        _ => "Recovers"
    };

    private static string Describe(CandidateEdge e)
    {
        var parts = new List<string>();
        if (e.Commands.Count > 0) parts.Add("cmd " + string.Join("+", e.Commands.Select(c => "“" + c + "”")));
        parts.Add(e.Contact switch { ContactRequirement.Hit => "needs hit", ContactRequirement.Contact => "needs contact", ContactRequirement.Guarded => "guard only", _ => "no contact needed" });
        parts.AddRange(e.Facets.Power.Select(p => $"power {p.Op} {p.Value:0.##}"));
        if (e.EarliestTick is { } t) parts.Add($"from tick {t}");
        if (e.Facets.CtrlRequired) parts.Add("needs ctrl");
        if (e.Unmodelled.Count > 0) parts.Add($"{e.Unmodelled.Count} unmodelled");
        return string.Join(" · ", parts);
    }

    private static string Tooltip(CandidateEdge e)
    {
        var lines = new List<string> { $"{ConfidenceStyle.Label(e.Confidence)} · controller {e.ControllerId}" };
        lines.AddRange(e.Evidence.Select(x => $"• {x.RuleId}: {EvidenceRules.Get(x.RuleId).Description}"));
        lines.AddRange(e.Unmodelled.Select(u => "⚠ unmodelled: " + u));
        lines.AddRange(e.Notes.Select(n => "ℹ " + n));
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ routes

    private async Task FindAsync()
    {
        if (_graph is null) return;
        _busy = true;
        try
        {
            if (!int.TryParse(_meterText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var meter) || meter < 0)
            {
                RouteStatus = "Meter must be a whole number, 0 or more.";
                return;
            }

            var options = new ComboOptions
            {
                From = _fromSelected && _center is not null && _graph.Move(_center) is { IsNeutral: false } ? _center : null,
                MaxMoves = _maxMoves,
                StartMeter = meter,
                HitConfirmOnly = _hitConfirmOnly,
                AllowLinks = _allowLinks,
                Top = 40
            };

            RouteStatus = "Searching…";
            var graph = _graph;
            var result = await Task.Run(() => ComboSearch.Find(graph, options));
            _routes = result.Routes;

            Routes.Clear();
            RouteSteps.Clear();
            foreach (var r in result.Routes)
            {
                var names = new List<string> { r.StartState == CandidateGraph.NeutralId ? string.Empty : ShortName(r.StartState) };
                names.AddRange(r.Steps.Select(s => ShortName(s.Move.StateId)));
                Routes.Add(new XRayRow(r.Key, string.Join(" → ", names.Where(n => n.Length > 0)),
                    $"{r.DamageKnown:0.##}{(r.DamageComplete ? string.Empty : "+")} damage · {r.MeterSpent} meter · {r.UnmodelledCount} unmodelled" +
                    (r.MinFrames is { } f ? $" · ≥ {f}f{(r.FramesComplete ? string.Empty : "?")}" : string.Empty),
                    r.Confidence, tooltip: string.Join("\n", r.Notes)));
            }

            RouteStatus = result.Routes.Count == 0
                ? "No candidate routes under these options. " + string.Join(" ", result.Warnings.Where(w => !w.StartsWith("Routes are candidates", StringComparison.Ordinal)))
                : $"{result.Routes.Count} candidate route(s), best first · {result.Expansions:N0} expansions{(result.Truncated ? " (search capped)" : string.Empty)} · candidates, not verified combos";
            SelectedRoute = Routes.FirstOrDefault();
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string ShortName(string stateId) => stateId.StartsWith("state:", StringComparison.Ordinal) ? stateId["state:".Length..] : stateId;

    private void ShowSteps(ComboRoute? route)
    {
        RouteSteps.Clear();
        if (route is null) return;
        var verdicts = _report is { } rep && rep.RouteKey == route.Key ? rep.Steps : null;
        foreach (var (s, i) in route.Steps.Select((s, i) => (s, i)))
        {
            var mark = verdicts is not null && i < verdicts.Count
                ? verdicts[i].Outcome switch { StepOutcome.Observed => "✓ ", StepOutcome.NotObserved => "✗ ", _ => "· " }
                : string.Empty;
            var seen = verdicts is not null && i < verdicts.Count && verdicts[i].Detail is { Length: > 0 } d ? " — " + d : string.Empty;
            RouteSteps.Add(new XRayRow(s.Move.StateId, $"{mark}{KindText(s.Edge.Kind)} → {s.Move.Name}" + (s.Damage is { } dmg ? $"  ({dmg:0.##} dmg)" : string.Empty),
                Describe(s.Edge) + (s.Move.PowerCost > 0 ? $" · costs {s.Move.PowerCost:0}" : string.Empty) + seen, s.Edge.Confidence, tooltip: Tooltip(s.Edge)));
        }

        foreach (var note in route.Notes.Take(4))
            RouteSteps.Add(new XRayRow("note", note, null, null));
    }
}
