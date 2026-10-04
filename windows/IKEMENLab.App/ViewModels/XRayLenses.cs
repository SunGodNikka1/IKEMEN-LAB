using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Query;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.App.ViewModels;

// ====================================================================== 1. Ability Atlas

/// <summary>Abilities grouped by (inferred) category. Selecting one reveals its commands, states, helpers, variables and victim paths elsewhere.</summary>
public sealed class AbilityAtlasLens : XRayLens
{
    private static readonly string[] CategoryOrder = ["Super", "Throw", "Projectile", "Counter", "Special", "Normal", "Defensive", "Summon", "Mobility", "Mode", "Other"];
    private readonly ObservableCollection<XRayRow> _rows = [];
    private readonly Dictionary<string, XRayRow> _byId = new(StringComparer.Ordinal);
    private XRayRow? _selected;
    private bool _suppress;
    private string _summary = string.Empty;

    public AbilityAtlasLens(XRayViewModel owner) : base(owner)
    {
        View = CollectionViewSource.GetDefaultView(_rows);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(XRayRow.Group)));
        View.SortDescriptions.Add(new SortDescription(nameof(XRayRow.GroupOrder), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(XRayRow.Title), ListSortDirection.Ascending));
    }

    public ICollectionView View { get; }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public XRayRow? SelectedRow
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value) && !_suppress && value is not null) Owner.Select(value.Id); }
    }

    public override void Build(SemanticIndex index)
    {
        _rows.Clear();
        _byId.Clear();
        foreach (var a in index.Of(ObjectKind.Ability))
        {
            var category = a.Prop("category") ?? "Other";
            var order = Array.IndexOf(CategoryOrder, category);
            var label = a.Labels.FirstOrDefault(l => l.Category == LabelCategories.AbilityCategory);
            var entry = a.Prop("entry") switch { "ai" => "AI decision", "command+ai" => "command or AI", _ => "command" };
            var technical = index.Names.IsRenamed(a.Id) ? $"{a.Name} · " : string.Empty;
            var row = new XRayRow(a.Id, index.NameOf(a.Id), $"{technical}{entry} → {a.Prop("entryState")?.Replace("state:", "State ")} · {a.Prop("members")} states",
                label?.Confidence ?? Confidence.Unknown, category, order < 0 ? 99 : order,
                label is null ? "No category heuristic matched" : $"Category is inferred: {label.RuleId} · {EvidenceRules.Get(label.RuleId).Description}");
            _rows.Add(row);
            _byId[a.Id] = row;
        }

        Summary = _rows.Count == 0
            ? "No abilities found: no ChangeState is gated by a command or by AILevel."
            : $"{_rows.Count} abilities · categories are inferred, membership follows the state graph";
    }

    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related)
    {
        foreach (var r in _rows) r.IsHighlighted = related.Contains(r.Id);
        if (id is not null && _byId.TryGetValue(id, out var row))
        {
            _suppress = true;
            SelectedRow = row;
            _suppress = false;
        }
    }
}

// ====================================================================== 2. Trigger Explorer

public sealed class GateNode : ObservableObject
{
    public GateNode(string title, string detail = "", Confidence? confidence = null, SourceRef? source = null, string? targetId = null)
    {
        Title = title;
        Detail = detail;
        Confidence = confidence;
        Source = source;
        TargetId = targetId;
    }

    public string Title { get; }
    public string Detail { get; }
    public Confidence? Confidence { get; }
    public SourceRef? Source { get; }
    public string? TargetId { get; }
    public string Glyph => ConfidenceStyle.Glyph(Confidence);
    public Brush GlyphBrush => ConfidenceStyle.Brush(Confidence);
    public bool HasGlyph => Confidence is not null;
    public bool HasDetail => Detail.Length > 0;
    public ObservableCollection<GateNode> Children { get; } = [];
}

/// <summary>Decomposes a controller's triggers into AND/OR structure with what each line means, and where the controller leads.</summary>
public sealed class TriggerExplorerLens : XRayLens
{
    private XRayRow? _selectedController;
    private bool _suppress;
    private string _header = "Select a state or controller";
    private string _facets = string.Empty;

    public TriggerExplorerLens(XRayViewModel owner) : base(owner) { }

    public ObservableCollection<XRayRow> Controllers { get; } = [];
    public ObservableCollection<GateNode> Nodes { get; } = [];
    public string Header { get => _header; private set => SetProperty(ref _header, value); }
    public string Facets { get => _facets; private set => SetProperty(ref _facets, value); }

    public XRayRow? SelectedController
    {
        get => _selectedController;
        set { if (SetProperty(ref _selectedController, value) && !_suppress && value is not null) Owner.Select(value.Id); }
    }

    public override void Build(SemanticIndex index) { }

    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related)
    {
        if (id is null) return;
        var (list, pick) = Context(index, id);
        if (list is null) return;

        if (!Controllers.Select(c => c.Id).SequenceEqual(list.Select(c => c.Id)))
        {
            Controllers.Clear();
            foreach (var c in list)
            {
                var type = c.Prop("type") ?? "controller";
                var targets = string.Join(", ", index.Outgoing(c.Id, RelationKind.ChangesState).Select(r => index.NameOf(r.To)));
                Controllers.Add(new XRayRow(c.Id, $"{Short(c.Id)}  {type}{(c.Prop("name") is { Length: > 0 } n ? " “" + n + "”" : string.Empty)}",
                    targets.Length > 0 ? "→ " + targets : FileLine(index, c.Source)));
            }
        }

        foreach (var row in Controllers) row.IsHighlighted = related.Contains(row.Id);
        var chosen = Controllers.FirstOrDefault(c => c.Id == pick) ?? Controllers.FirstOrDefault();
        _suppress = true;
        SelectedController = chosen;
        _suppress = false;
        Show(index, chosen is null ? null : index.Get(chosen.Id));
    }

    private static (List<SemanticObject>? List, string? Pick) Context(SemanticIndex index, string id)
    {
        var obj = index.Get(id)!;
        switch (obj.Kind)
        {
            case ObjectKind.Controller or ObjectKind.HitDef:
            {
                var ctrlId = obj.Kind == ObjectKind.HitDef ? obj.ParentId! : id;
                var owner = index.OwnerState(ctrlId);
                return owner is null ? (null, null) : (index.ControllersOf(owner.Id).ToList(), ctrlId);
            }
            case ObjectKind.State:
            {
                var list = index.ControllersOf(id).ToList();
                var pick = list.FirstOrDefault(c => index.Outgoing(c.Id, RelationKind.ChangesState).Count > 0 && c.Gate is { IsEmpty: false })?.Id ?? list.FirstOrDefault()?.Id;
                return (list, pick);
            }
            case ObjectKind.Ability:
            {
                var list = (obj.Prop("entryControllers") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(index.Get).OfType<SemanticObject>().ToList();
                return (list, list.FirstOrDefault()?.Id);
            }
            case ObjectKind.Command:
            {
                var list = index.Incoming(id, RelationKind.ReferencesCommand).Select(r => index.Get(r.From)).OfType<SemanticObject>().DistinctBy(o => o.Id).ToList();
                return (list, list.FirstOrDefault()?.Id);
            }
            case ObjectKind.Variable:
            {
                var list = index.VarUsage(id).Select(u => u.Controller).Where(c => c.Kind == ObjectKind.Controller).DistinctBy(o => o.Id).Take(40).ToList();
                return (list, list.FirstOrDefault()?.Id);
            }
            case ObjectKind.Helper:
            {
                var list = index.Incoming(id, RelationKind.SpawnsHelper).Select(r => index.Get(r.From)).OfType<SemanticObject>().DistinctBy(o => o.Id).ToList();
                return (list, list.FirstOrDefault()?.Id);
            }
            default:
                return (null, null);
        }
    }

    private void Show(SemanticIndex index, SemanticObject? c)
    {
        Nodes.Clear();
        if (c is null)
        {
            Header = "This object has no triggers to explore";
            Facets = string.Empty;
            return;
        }

        Header = $"{c.Prop("type")} — {c.Id}";
        var gate = c.Gate;
        var root = new GateNode($"{c.Prop("type")}{(c.Prop("name") is { Length: > 0 } n ? " “" + n + "”" : string.Empty)}", FileLine(index, c.Source), null, c.Source);

        if (gate is null || gate.IsEmpty)
        {
            root.Children.Add(new GateNode("No triggers — this controller never fires", "a controller needs trigger1", Confidence.Unknown, c.Source));
        }
        else
        {
            if (gate.TriggerAll.Count > 0)
            {
                var all = new GateNode("ALL of these must hold", "triggerall");
                foreach (var l in gate.TriggerAll) all.Children.Add(Line(l));
                root.Children.Add(all);
            }

            if (gate.Branches.Count > 0)
            {
                var any = new GateNode(gate.Branches.Select(b => b.Number).Distinct().Count() > 1 ? "AND at least ONE of these groups" : "AND this group", "trigger1, trigger2 … are alternatives");
                foreach (var group in gate.Branches.GroupBy(b => b.Number))
                {
                    var first = group.First();
                    var g = new GateNode($"trigger{group.Key}: all of", group.Count() > 1 ? $"expands to {group.Count()} alternatives (OR-ed commands)" : string.Empty);
                    foreach (var l in first.Lines) g.Children.Add(Line(l));
                    foreach (var alt in group) g.Children.Add(new GateNode(FacetText(alt.Facets), alt.Facets.OtherConditions > 0
                        ? $"{alt.Facets.OtherConditions} condition(s) are not a recognised shape, so this summary is incomplete" : "every condition is accounted for", null, first.Lines.FirstOrDefault()?.Source));
                    any.Children.Add(g);
                }

                root.Children.Add(any);
            }
        }

        var then = new GateNode("Then it does");
        foreach (var r in index.Outgoing(c.Id).Concat(index.Outgoing(c.Id + "/hitdef")))
        {
            if (r.Kind is RelationKind.Contains or RelationKind.ReferencesCommand or RelationKind.GatedByPower or RelationKind.EntryPoint or RelationKind.ActiveAtFrame) continue;
            var target = index.Get(r.To);
            then.Children.Add(new GateNode($"{Describe(r.Kind)} {(target is null ? r.To : index.NameOf(r.To))}", r.Prop("value") is { } v ? "value " + v : r.Prop("scope") is { } sc ? "scope " + sc : string.Empty,
                r.Confidence, r.Evidence.FirstOrDefault()?.Source, r.To));
        }

        if (then.Children.Count > 0) root.Children.Add(then);
        Nodes.Add(root);
        Facets = gate is null ? string.Empty : "Legend: " + ConfidenceStyle.Legend;
    }

    private static GateNode Line(TriggerLine l)
    {
        if (l.Expression is RawExpr raw)
            return new GateNode(l.Text, "could not parse: " + raw.Error + " — treated as unknown", Confidence.Unknown, l.Source);
        var f = ExprAnalyzer.Analyze(l.Expression);
        var tags = new List<string>();
        tags.AddRange(f.Commands.Select(c => (c.Negated ? "not command " : "command ") + "“" + c.Name + "”"));
        tags.AddRange(f.Power.Select(p => $"power {p.Op} {p.Value:0.##}"));
        tags.AddRange(f.Time.Select(p => $"time {p.Op} {p.Value:0.##}"));
        tags.AddRange(f.AnimElems.Select(p => $"animelem {p.Op} {p.Value:0.##}"));
        tags.AddRange(f.Contact.Select(c => c));
        if (f.ReadsCtrl) tags.Add("ctrl");
        if (f.ReadsAiLevel) tags.Add("AILevel");
        tags.AddRange(f.Vars.Select(v => $"{(v.IsWrite ? "writes" : "reads")} {v.Kind.ToString().ToLowerInvariant()}({(v.Index?.ToString(CultureInfo.InvariantCulture) ?? "?")}){(v.Scope != EntityScope.Self ? " via " + v.Scope.ToString().ToLowerInvariant() : string.Empty)}"));
        return new GateNode(l.Text, string.Join(" · ", tags), Confidence.StaticProven, l.Source);
    }

    private static string FacetText(GateFacets f)
    {
        var parts = new List<string>();
        if (f.Commands.Count > 0) parts.Add("command " + string.Join(" + ", f.Commands.Select(c => "“" + c + "”")));
        if (f.NegatedCommands.Count > 0) parts.Add("not " + string.Join(", ", f.NegatedCommands));
        if (f.Contact.Count > 0) parts.Add(string.Join("+", f.Contact));
        parts.AddRange(f.Power.Select(p => $"power {p.Op} {p.Value:0.##}"));
        parts.AddRange(f.Time.Select(p => $"time {p.Op} {p.Value:0.##}"));
        parts.AddRange(f.AnimElem.Select(p => $"animelem {p.Op} {p.Value:0.##}"));
        if (f.CtrlRequired) parts.Add("needs ctrl");
        if (f.StateTypes.Count > 0) parts.Add("state type " + string.Join("/", f.StateTypes));
        if (f.ReadsAiLevel) parts.Add("AI");
        return parts.Count == 0 ? "Summary: no recognised conditions" : "Summary: " + string.Join(" · ", parts);
    }

    private static string Describe(RelationKind k) => k switch
    {
        RelationKind.ChangesState => "→ changes to",
        RelationKind.SetsVictimState => "→ puts the victim in",
        RelationKind.SetsAttackerState => "→ puts the attacker in",
        RelationKind.SpawnsHelper => "spawns",
        RelationKind.SpawnsProjectile => "fires",
        RelationKind.WritesVar => "writes",
        RelationKind.ReadsVar => "reads",
        RelationKind.ResetsVar => "clears",
        RelationKind.UsesAnim => "plays",
        RelationKind.DefinesHitDef => "defines",
        RelationKind.Binds => "binds",
        RelationKind.AffectsTarget => "affects",
        RelationKind.ResourceCost => "changes",
        _ => k.ToString()
    };
}

// ====================================================================== 3. State Graph

public sealed class GraphNodeVM : ObservableObject
{
    private bool _isHighlighted;
    public GraphNodeVM(string id, string title, string subtitle, double x, double y, bool isCenter, Confidence? confidence)
    {
        Id = id; Title = title; Subtitle = subtitle; X = x; Y = y; IsCenter = isCenter; Confidence = confidence;
    }

    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public double X { get; }
    public double Y { get; }
    public bool IsCenter { get; }
    public Confidence? Confidence { get; }
    public string Glyph => ConfidenceStyle.Glyph(Confidence);
    public Brush GlyphBrush => ConfidenceStyle.Brush(Confidence);
    public bool IsHighlighted { get => _isHighlighted; set => SetProperty(ref _isHighlighted, value); }
}

public sealed record GraphEdgeVM(double X1, double Y1, double X2, double Y2, Brush Stroke, DoubleCollection? Dash, double Thickness,
    double MidX, double MidY, string MidGlyph, Brush MidBrush, string Tooltip);

/// <summary>Neighbourhood of one state as a directed graph. Solid = static proven, dashed = inferred, dotted = unknown; the mid-edge mark repeats it.</summary>
public sealed class StateGraphLens : XRayLens
{
    public const double NodeWidth = 176;
    public const double NodeHeight = 44;
    private static readonly Brush ChangeBrush = Frozen(Color.FromRgb(0xE4, 0xE4, 0xE7));
    private static readonly Brush VictimBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush AttackerBrush = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush HelperBrush = Frozen(Color.FromRgb(0x64, 0xA8, 0xFF));

    private StateGraph? _graph;
    private string? _center;
    private int _depth = 2;
    private string _caption = "Select a state";
    private double _canvasWidth = 600;
    private double _canvasHeight = 300;

    public StateGraphLens(XRayViewModel owner) : base(owner) { }

    public ObservableCollection<GraphNodeVM> Nodes { get; } = [];
    public ObservableCollection<GraphEdgeVM> Edges { get; } = [];
    public string Caption { get => _caption; private set => SetProperty(ref _caption, value); }
    public double CanvasWidth { get => _canvasWidth; private set => SetProperty(ref _canvasWidth, value); }
    public double CanvasHeight { get => _canvasHeight; private set => SetProperty(ref _canvasHeight, value); }

    public double Depth
    {
        get => _depth;
        set
        {
            var d = (int)Math.Clamp(Math.Round(value), 1, 3);
            if (d == _depth) return;
            _depth = d;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DepthText));
            if (Owner.Index is { } index && _center is not null) Layout(index, Owner.SelectedId);
        }
    }

    public string DepthText => _depth == 1 ? "1 hop" : $"{_depth} hops";

    public override void Build(SemanticIndex index) => _graph = StateGraph.Build(index);

    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related)
    {
        if (id is null || _graph is null) return;
        var center = CenterFor(index, id);
        if (center is not null) _center = center;
        if (_center is null) return;
        Layout(index, id, related);
    }

    private void Layout(SemanticIndex index, string? selected, IReadOnlySet<string>? related = null)
    {
        if (_graph is null || _center is null) return;
        related ??= new HashSet<string>(StringComparer.Ordinal);
        var layout = GraphLayout.Neighborhood(_graph, _center, _depth);
        const double margin = 16;

        Nodes.Clear();
        Edges.Clear();
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        foreach (var n in layout.Nodes)
        {
            var obj = index.Get(n.Id);
            var name = index.NameOf(n.Id);
            var detail = new List<string>();
            if (obj?.Prop("p.type") is { } t) detail.Add(t);
            if (obj?.Prop("p.movetype") is { } m) detail.Add("move " + m);
            if (obj?.Labels.FirstOrDefault(l => l.Category == LabelCategories.Name)?.Text is { } author) detail.Add("“" + Trim(author, 22) + "”");
            if (obj?.Prop("common") == "true") detail.Add("common");
            var confidence = obj is null ? Confidence.Unknown : obj.IsStub ? (obj.Prop("engineCommon") == "true" ? Confidence.Inferred : Confidence.Unknown) : Confidence.StaticProven;
            var node = new GraphNodeVM(n.Id, name, string.Join(" · ", detail), n.X + margin, n.Y + margin, n.IsCenter, confidence)
            {
                IsHighlighted = related.Contains(n.Id) || n.Id == selected
            };
            Nodes.Add(node);
            positions[n.Id] = (node.X, node.Y);
        }

        foreach (var e in layout.Edges)
        {
            if (e.From == e.To || !positions.TryGetValue(e.From, out var a) || !positions.TryGetValue(e.To, out var b)) continue;
            double x1, y1, x2, y2;
            if (b.X > a.X + 1) { x1 = a.X + NodeWidth; y1 = a.Y + NodeHeight / 2; x2 = b.X; y2 = b.Y + NodeHeight / 2; }
            else if (b.X < a.X - 1) { x1 = a.X; y1 = a.Y + NodeHeight / 2; x2 = b.X + NodeWidth; y2 = b.Y + NodeHeight / 2; }
            else if (b.Y >= a.Y) { x1 = a.X + NodeWidth / 2; y1 = a.Y + NodeHeight; x2 = b.X + NodeWidth / 2; y2 = b.Y; }
            else { x1 = a.X + NodeWidth / 2; y1 = a.Y; x2 = b.X + NodeWidth / 2; y2 = b.Y + NodeHeight; }

            var kindBrush = e.Kind switch
            {
                StateEdgeKind.Victim => VictimBrush,
                StateEdgeKind.Attacker => AttackerBrush,
                StateEdgeKind.HelperSpawn => HelperBrush,
                _ => ChangeBrush
            };
            var kindText = e.Kind switch
            {
                StateEdgeKind.Victim => "puts the victim in this state",
                StateEdgeKind.Attacker => "puts the attacker in this state on hit",
                StateEdgeKind.HelperSpawn => "spawns a helper that runs this state (" + e.ViaHelper + ")",
                _ => "ChangeState / SelfState"
            };
            Edges.Add(new GraphEdgeVM(x1, y1, x2, y2, kindBrush, ConfidenceStyle.Dash(e.Confidence), e.Confidence == Confidence.StaticProven ? 2 : 1.5,
                (x1 + x2) / 2, (y1 + y2) / 2 - 8, ConfidenceStyle.Glyph(e.Confidence), ConfidenceStyle.Brush(e.Confidence),
                $"{kindText}\n{ConfidenceStyle.Label(e.Confidence)} · via {string.Join(", ", e.ControllerIds.Take(4).Select(Short))}"));
        }

        CanvasWidth = layout.Width + margin * 2;
        CanvasHeight = layout.Height + margin * 2;
        var centerName = index.NameOf(_center);
        Caption = $"{centerName} · {layout.Nodes.Count} states within {DepthText}" + (layout.Truncated ? " (trimmed)" : string.Empty)
                  + "   — white = ChangeState, red = victim, orange = attacker, blue = helper";
    }

    private static string? CenterFor(SemanticIndex index, string id)
    {
        var obj = index.Get(id);
        if (obj is null) return null;
        switch (obj.Kind)
        {
            case ObjectKind.State: return id;
            case ObjectKind.Controller or ObjectKind.HitDef or ObjectKind.Clsn: return index.OwnerState(id)?.Id;
            case ObjectKind.Ability: return obj.Prop("entryState");
            case ObjectKind.Helper: return index.Outgoing(id, RelationKind.HelperRunsState).FirstOrDefault()?.To;
            default: return null;
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

// ====================================================================== 4. Variable Map

/// <summary>Every var/fvar/sysvar with who reads, writes and clears it. Names are Inferred labels and are marked as such.</summary>
public sealed class VariableMapLens : XRayLens
{
    private readonly ObservableCollection<XRayRow> _rows = [];
    private readonly Dictionary<string, XRayRow> _byId = new(StringComparer.Ordinal);
    private XRayRow? _selected;
    private XRayRow? _selectedUse;
    private bool _suppress;
    private string _filter = string.Empty;
    private string _traits = string.Empty;
    private string _usesHeader = "Select a variable";

    public VariableMapLens(XRayViewModel owner) : base(owner)
    {
        View = CollectionViewSource.GetDefaultView(_rows);
        View.Filter = o => o is XRayRow r && (_filter.Length == 0 ||
                                              r.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                                              r.Subtitle.Contains(_filter, StringComparison.OrdinalIgnoreCase));
    }

    public ICollectionView View { get; }
    public ObservableCollection<XRayRow> Uses { get; } = [];
    public string Traits { get => _traits; private set => SetProperty(ref _traits, value); }
    public string UsesHeader { get => _usesHeader; private set => SetProperty(ref _usesHeader, value); }

    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) View.Refresh(); }
    }

    public XRayRow? SelectedRow
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value) && !_suppress && value is not null) Owner.Select(value.Id); }
    }

    public XRayRow? SelectedUse
    {
        get => _selectedUse;
        set { if (SetProperty(ref _selectedUse, value) && !_suppress && value is not null) Owner.Select(value.Id); }
    }

    public override void Build(SemanticIndex index)
    {
        _rows.Clear();
        _byId.Clear();
        foreach (var v in index.Of(ObjectKind.Variable).OrderBy(v => v.Prop("dynamic") == "true").ThenBy(v => v.Prop("kind"), StringComparer.Ordinal)
                     .ThenBy(v => int.TryParse(v.Prop("index"), out var n) ? n : int.MaxValue))
        {
            var uses = index.VarUsage(v.Id);
            var r = uses.Count(u => u.Relationship.Kind == RelationKind.ReadsVar);
            var w = uses.Count(u => u.Relationship.Kind == RelationKind.WritesVar);
            var z = uses.Count(u => u.Relationship.Kind == RelationKind.ResetsVar);
            var name = v.Labels.FirstOrDefault(l => l.Category == LabelCategories.VariableName)?.Text;
            var confidence = v.Prop("dynamic") == "true" ? Confidence.Unknown : Confidence.StaticProven;
            var row = new XRayRow(v.Id, index.NameOf(v.Id), $"read {r} · written {w} · cleared {z}" + (name is null ? string.Empty : "   ◐ " + name),
                confidence, tooltip: v.Prop("dynamic") == "true" ? "The index expression is not a literal, so which variable is touched is unknown" : "Index is a literal in the files");
            _rows.Add(row);
            _byId[v.Id] = row;
        }
    }

    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related)
    {
        foreach (var r in _rows) r.IsHighlighted = related.Contains(r.Id);
        if (id is null) return;
        if (_byId.TryGetValue(id, out var row))
        {
            _suppress = true;
            SelectedRow = row;
            _suppress = false;
            ShowUses(index, id);
        }
    }

    private void ShowUses(SemanticIndex index, string variableId)
    {
        Uses.Clear();
        var v = index.Get(variableId)!;
        UsesHeader = $"{index.NameOf(v.Id)} — who touches it";
        Traits = string.Join("  ·  ", v.Labels.Select(l => "◐ " + l.Text));
        foreach (var u in index.VarUsage(variableId).OrderBy(u => u.Relationship.Kind).ThenBy(u => u.Controller.Id, StringComparer.Ordinal))
        {
            var verb = u.Relationship.Kind switch { RelationKind.WritesVar => "writes", RelationKind.ResetsVar => "clears", _ => "reads" };
            var scope = u.Relationship.Prop("scope") ?? "self";
            var arg = u.Relationship.Prop("scopeArg");
            var detail = u.Relationship.Prop("value") is { } val ? $" = {val}" : u.Relationship.Prop("step") is { } st ? $" += {st}" : string.Empty;
            Uses.Add(new XRayRow(u.Controller.Id, $"{verb}{detail}  —  {u.Controller.Name}",
                $"{(u.State is null ? "?" : index.NameOf(u.State.Id))} · scope {scope}{(arg is null ? string.Empty : "(" + arg + ")")}",
                u.Relationship.Confidence, tooltip: u.Relationship.Evidence.FirstOrDefault() is { } e ? $"{e.RuleId} · {EvidenceRules.Get(e.RuleId).Description}" : null));
        }
    }
}

// ====================================================================== 5. Helper Tree

public sealed class TreeNodeVM : ObservableObject
{
    private bool _isHighlighted;
    private bool _isExpanded;

    public TreeNodeVM(string id, string title, string subtitle = "", Confidence? confidence = null, bool expanded = false)
    {
        Id = id; Title = title; Subtitle = subtitle; Confidence = confidence; _isExpanded = expanded;
    }

    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public Confidence? Confidence { get; }
    public string Glyph => ConfidenceStyle.Glyph(Confidence);
    public Brush GlyphBrush => ConfidenceStyle.Brush(Confidence);
    public bool HasGlyph => Confidence is not null;
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool IsHighlighted { get => _isHighlighted; set => SetProperty(ref _isHighlighted, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public ObservableCollection<TreeNodeVM> Children { get; } = [];
}

/// <summary>The helper and projectile family: who spawns each, which states it runs, what it hits with and which variables it touches.</summary>
public sealed class HelperTreeLens : XRayLens
{
    private readonly List<TreeNodeVM> _all = [];

    public HelperTreeLens(XRayViewModel owner) : base(owner) { }

    public ObservableCollection<TreeNodeVM> Roots { get; } = [];
    public string Summary { get; private set; } = string.Empty;

    public override void Build(SemanticIndex index)
    {
        Roots.Clear();
        _all.Clear();
        var root = new TreeNodeVM(index.CharacterId, index.Get(index.CharacterId)?.Name ?? "Character", "root", null, expanded: true);
        _all.Add(root);

        foreach (var helper in index.Of(ObjectKind.Helper).OrderBy(h => h.Id, StringComparer.Ordinal))
        {
            var role = string.Join("; ", helper.Labels.Select(l => l.Text));
            var node = new TreeNodeVM(helper.Id, index.NameOf(helper.Id) + (helper.Prop("name") is { Length: > 0 } n ? $" “{n}”" : string.Empty),
                role.Length > 0 ? "◐ " + role : string.Empty, null, expanded: true);
            foreach (var spawn in index.Incoming(helper.Id, RelationKind.SpawnsHelper))
                node.Children.Add(new TreeNodeVM(spawn.From, "spawned by " + Where(index, spawn.From), GateShort(index, spawn.From), spawn.Confidence));

            foreach (var run in index.Outgoing(helper.Id, RelationKind.HelperRunsState))
            {
                var state = new TreeNodeVM(run.To, "runs " + (index.NameOf(run.To)), string.Empty, run.Confidence, expanded: true);
                foreach (var ctrl in index.ControllersOf(run.To))
                {
                    foreach (var hit in index.Outgoing(ctrl.Id, RelationKind.DefinesHitDef))
                        state.Children.Add(new TreeNodeVM(hit.To, "HitDef " + (index.Get(hit.To)?.Prop("p.attr") ?? string.Empty), "damage " + (index.Get(hit.To)?.Prop("p.damage") ?? "?"), hit.Confidence));
                    foreach (var w in index.Outgoing(ctrl.Id, RelationKind.WritesVar).GroupBy(r => r.To).Select(g => g.First()))
                        state.Children.Add(new TreeNodeVM(w.To, "writes " + (index.NameOf(w.To)), "scope " + w.Prop("scope"), w.Confidence));
                    if (ctrl.Prop("type") is "bindtoroot" or "bindtoparent" or "bindtotarget" or "bind" or "destroyself")
                        state.Children.Add(new TreeNodeVM(ctrl.Id, ctrl.Prop("type")!, GateShort(index, ctrl.Id), Confidence.StaticProven));
                }

                node.Children.Add(state);
            }

            root.Children.Add(node);
            _all.Add(node);
        }

        var projectiles = index.Of(ObjectKind.Projectile).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        if (projectiles.Count > 0)
        {
            var group = new TreeNodeVM("group:projectiles", "Projectiles", string.Empty, null, expanded: true);
            foreach (var p in projectiles)
            {
                var node = new TreeNodeVM(p.Id, index.NameOf(p.Id), string.Empty, null, expanded: true);
                foreach (var spawn in index.Incoming(p.Id, RelationKind.SpawnsProjectile))
                    node.Children.Add(new TreeNodeVM(spawn.From, "fired by " + Where(index, spawn.From), GateShort(index, spawn.From), spawn.Confidence));
                foreach (var anim in index.Outgoing(p.Id, RelationKind.UsesAnim))
                    node.Children.Add(new TreeNodeVM(anim.To, $"{anim.Prop("role") ?? "animation"}: {index.NameOf(anim.To)}", string.Empty, anim.Confidence));
                group.Children.Add(node);
                _all.Add(node);
            }

            root.Children.Add(group);
        }

        Roots.Add(root);
        Summary = root.Children.Count == 0 ? "This character spawns no helpers or projectiles." : $"{index.Of(ObjectKind.Helper).Count()} helpers · {projectiles.Count} projectiles";
    }

    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related)
    {
        foreach (var n in Flatten(Roots)) n.IsHighlighted = related.Contains(n.Id) && !n.Id.StartsWith("group:", StringComparison.Ordinal);
    }

    private static IEnumerable<TreeNodeVM> Flatten(IEnumerable<TreeNodeVM> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    private static string Where(SemanticIndex index, string controllerId) =>
        $"{(index.OwnerState(controllerId) is { } owner ? index.NameOf(owner.Id) : "?")} / {Short(controllerId)}";

    private static string GateShort(SemanticIndex index, string controllerId)
    {
        var text = SemanticIndex.GateText(index.Get(controllerId)?.Gate);
        return text.Length <= 90 ? text : text[..89] + "…";
    }
}

// ====================================================================== 6. Animation / CLSN Timeline

public sealed record MarkerVM(string Id, string Text, string Glyph, Brush Brush, string Tooltip);

public sealed class FrameVM : ObservableObject
{
    private bool _isSelected;
    public FrameVM(string id, string spriteId, string label, string ticks, double width, Confidence spriteConfidence)
    {
        Id = id; SpriteId = spriteId; Label = label; TicksText = ticks; Width = width; SpriteConfidence = spriteConfidence;
    }

    public string Id { get; }
    public string SpriteId { get; }
    public string Label { get; }
    public string TicksText { get; }
    public double Width { get; }
    public Confidence SpriteConfidence { get; }
    public string SpriteGlyph => ConfidenceStyle.Glyph(SpriteConfidence);
    public Brush SpriteBrush => ConfidenceStyle.Brush(SpriteConfidence);
    public ObservableCollection<MarkerVM> Markers { get; } = [];
    public bool HasMarkers => Markers.Count > 0;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed record ClsnBoxVM(double X, double Y, double Width, double Height, Brush Stroke, Brush Fill, string Tooltip);

/// <summary>Frames of the selected animation with their durations, CLSN boxes and the controllers gated on them. Sprites open the Sprite Inspector.</summary>
public sealed class AnimationTimelineLens : XRayLens
{
    public const double BoxScale = 2.4;
    public const double StageWidth = 460;
    public const double StageHeight = 340;
    public const double OriginX = 230;
    public const double OriginY = 290;
    private static readonly Brush AttackStroke = Frozen(Color.FromRgb(0xFF, 0x4D, 0x4D));
    private static readonly Brush AttackFill = Frozen(Color.FromArgb(0x33, 0xFF, 0x4D, 0x4D));
    private static readonly Brush HurtStroke = Frozen(Color.FromRgb(0x4D, 0x9F, 0xFF));
    private static readonly Brush HurtFill = Frozen(Color.FromArgb(0x33, 0x4D, 0x9F, 0xFF));

    private string? _animId;
    private FrameVM? _selectedFrame;
    private bool _suppress;
    private string _title = "Select a state, animation or frame";
    private string _detail = string.Empty;

    public AnimationTimelineLens(XRayViewModel owner) : base(owner)
    {
        OpenSpriteCommand = new RelayCommand(() =>
        {
            if (_selectedFrame is not null) Owner.OpenSprite(_selectedFrame.SpriteId);
        }, () => _selectedFrame is not null);
    }

    public ObservableCollection<FrameVM> Frames { get; } = [];
    public ObservableCollection<ClsnBoxVM> Boxes { get; } = [];
    public ICommand OpenSpriteCommand { get; }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public FrameVM? SelectedFrame
    {
        get => _selectedFrame;
        set
        {
            var old = _selectedFrame;
            if (!SetProperty(ref _selectedFrame, value)) return;
            if (old is not null) old.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            if (Owner.Index is { } index) ShowBoxes(index, value);
            CommandManager.InvalidateRequerySuggested();
            if (!_suppress && value is not null) Owner.Select(value.Id);
        }
    }

    public override void Build(SemanticIndex index) { }

    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related)
    {
        if (id is null) return;
        var anim = AnimFor(index, id);
        if (anim is null) return;

        if (anim != _animId) LoadAnimation(index, anim);
        _suppress = true;
        try
        {
            var target = index.Get(id)?.Kind == ObjectKind.AnimFrame ? Frames.FirstOrDefault(f => f.Id == id)
                         : index.Get(id)?.Kind == ObjectKind.Clsn ? Frames.FirstOrDefault(f => f.Id == index.Get(id)!.ParentId) : null;
            SelectedFrame = target ?? (related.Count > 0 ? Frames.FirstOrDefault(f => related.Contains(f.Id)) : null) ?? SelectedFrame ?? Frames.FirstOrDefault();
        }
        finally
        {
            _suppress = false;
        }
    }

    private static string? AnimFor(SemanticIndex index, string id)
    {
        var obj = index.Get(id);
        if (obj is null) return null;
        switch (obj.Kind)
        {
            case ObjectKind.Animation: return id;
            case ObjectKind.AnimFrame: return obj.ParentId;
            case ObjectKind.Clsn: return index.Get(obj.ParentId ?? string.Empty)?.ParentId;
            case ObjectKind.State: return index.Outgoing(id, RelationKind.UsesAnim).FirstOrDefault()?.To;
            case ObjectKind.Controller or ObjectKind.HitDef:
                return index.OwnerState(id) is { } s ? index.Outgoing(s.Id, RelationKind.UsesAnim).FirstOrDefault()?.To : null;
            case ObjectKind.Ability:
                return obj.Prop("entryState") is { } e ? index.Outgoing(e, RelationKind.UsesAnim).FirstOrDefault()?.To : null;
            case ObjectKind.Projectile:
                return index.Outgoing(id, RelationKind.UsesAnim).FirstOrDefault()?.To;
            default: return null;
        }
    }

    private void LoadAnimation(SemanticIndex index, string animId)
    {
        _animId = animId;
        Frames.Clear();
        Boxes.Clear();
        _selectedFrame = null;
        var anim = index.Get(animId)!;
        if (anim.IsStub)
        {
            Title = index.NameOf(anim.Id);
            Detail = "Referenced by the character but not defined in its AIR file.";
            return;
        }

        Title = $"{index.NameOf(anim.Id)} — {anim.Prop("frames")} frames, {anim.Prop("totalTicks")} ticks" + (anim.Prop("endsInfinite") == "true" ? " + holds forever" : string.Empty);
        Detail = (anim.Prop("loopStart") is { } ls ? $"loops from frame {ls}   " : string.Empty) + "● sprite in the SFF   ? sprite missing   markers show controllers gated on that frame (● AnimElem, ◐ time)";

        foreach (var contains in index.Outgoing(animId, RelationKind.Contains))
        {
            var f = index.Get(contains.To);
            if (f is null || f.Kind != ObjectKind.AnimFrame) continue;
            var ticks = int.TryParse(f.Prop("ticks"), out var t) ? t : 1;
            var spriteId = $"sprite:{f.Prop("group")},{f.Prop("index")}";
            var sprite = index.Outgoing(f.Id, RelationKind.AnimUsesSprite).FirstOrDefault();
            var frame = new FrameVM(f.Id, spriteId, $"{f.Prop("group")},{f.Prop("index")}", ticks < 0 ? "∞" : ticks + "t",
                ticks < 0 ? 72 : Math.Clamp(ticks * 8, 46, 260), sprite?.Confidence ?? Confidence.Unknown);
            foreach (var m in index.Incoming(f.Id, RelationKind.ActiveAtFrame))
            {
                var ctrl = index.Get(m.From);
                frame.Markers.Add(new MarkerVM(m.From, $"{ctrl?.Prop("type") ?? "controller"} {m.Prop("gate")}", ConfidenceStyle.Glyph(m.Confidence), ConfidenceStyle.Brush(m.Confidence),
                    $"{ConfidenceStyle.Label(m.Confidence)} · {m.Evidence[0].RuleId} · {EvidenceRules.Get(m.Evidence[0].RuleId).Description}"));
            }

            Frames.Add(frame);
        }
    }

    private void ShowBoxes(SemanticIndex index, FrameVM? frame)
    {
        Boxes.Clear();
        if (frame is null) return;
        foreach (var rel in index.Outgoing(frame.Id, RelationKind.HasClsn))
        {
            var c = index.Get(rel.To);
            if (c is null) continue;
            double N(string k) => double.TryParse(c.Prop(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var x1 = N("x1"); var y1 = N("y1"); var x2 = N("x2"); var y2 = N("y2");
            var attack = c.Prop("type") == "1";
            Boxes.Add(new ClsnBoxVM(OriginX + Math.Min(x1, x2) * BoxScale, OriginY + Math.Min(y1, y2) * BoxScale,
                Math.Abs(x2 - x1) * BoxScale, Math.Abs(y2 - y1) * BoxScale, attack ? AttackStroke : HurtStroke, attack ? AttackFill : HurtFill,
                $"{(attack ? "Clsn1 (attack)" : "Clsn2 (hurt)")} {x1},{y1} → {x2},{y2}{(c.Prop("inherited") == "true" ? "  (inherited from Default)" : string.Empty)}"));
        }
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
