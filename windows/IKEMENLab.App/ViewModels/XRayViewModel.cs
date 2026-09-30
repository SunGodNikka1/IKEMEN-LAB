using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.App.ViewModels;

public enum XRayLensKind { Atlas, Triggers, Graph, Variables, Helpers, Timeline, Combos }

/// <summary>
/// Character X-Ray workspace: one <see cref="SemanticIndex"/>, one selection, six lenses. Selecting anything in any lens
/// (or in the details panel) updates every other lens and the details/source panel.
/// </summary>
public sealed class XRayViewModel : ObservableObject
{
    private readonly string _root;
    private readonly CharacterEntry _entry;
    private readonly Stack<string> _history = new();
    private SemanticIndex? _index;
    private string? _selectedId;
    private bool _navigating;
    private bool _isLoading = true;
    private string _status = "Reading the character…";

    private string _title = string.Empty;
    private string _kindText = string.Empty;
    private string _idText = string.Empty;
    private string _sourceText = string.Empty;
    private string _sourceFile = string.Empty;
    private IReadOnlyList<LabelChip> _labels = [];
    private IReadOnlyList<DetailSection> _sections = [];
    private IReadOnlyList<SourceLine> _sourceLines = [];
    private string _searchText = string.Empty;
    private XRayLensKind _activeLens = XRayLensKind.Atlas;
    private IReadOnlyList<XRayRow> _searchResults = [];

    public XRayViewModel(string root, CharacterEntry entry, string displayName)
    {
        _root = root;
        _entry = entry;
        CharacterName = displayName;
        Atlas = new AbilityAtlasLens(this);
        Triggers = new TriggerExplorerLens(this);
        Graph = new StateGraphLens(this);
        Variables = new VariableMapLens(this);
        Helpers = new HelperTreeLens(this);
        Timeline = new AnimationTimelineLens(this);
        Combos = new ComboLens(this);
        Lenses = [Atlas, Triggers, Graph, Variables, Helpers, Timeline, Combos];

        SelectCommand = new RelayCommand(p => { if (p is string id) Select(id); });
        BackCommand = new RelayCommand(Back, () => _history.Count > 1);
        OpenFileCommand = new RelayCommand(OpenFile, () => _sourceFile.Length > 0);
        OpenSpriteCommand = new RelayCommand(p => { if (p is string id) OpenSprite(id); });
        CopyIdCommand = new RelayCommand(() =>
        {
            try { if (_selectedId is not null) Clipboard.SetText(_selectedId); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
        });
    }

    public string CharacterName { get; }
    public XRayLensKind ActiveLens { get => _activeLens; set => SetProperty(ref _activeLens, value); }
    public string Legend => ConfidenceStyle.Legend;
    public SemanticIndex? Index => _index;

    public AbilityAtlasLens Atlas { get; }
    public TriggerExplorerLens Triggers { get; }
    public StateGraphLens Graph { get; }
    public VariableMapLens Variables { get; }
    public HelperTreeLens Helpers { get; }
    public AnimationTimelineLens Timeline { get; }
    public ComboLens Combos { get; }
    public IReadOnlyList<XRayLens> Lenses { get; }

    public ICommand SelectCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenSpriteCommand { get; }
    public ICommand CopyIdCommand { get; }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? SelectedId => _selectedId;

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string KindText { get => _kindText; private set => SetProperty(ref _kindText, value); }
    public string IdText { get => _idText; private set => SetProperty(ref _idText, value); }
    public string SourceText { get => _sourceText; private set => SetProperty(ref _sourceText, value); }
    public bool HasSource => _sourceFile.Length > 0;
    public IReadOnlyList<LabelChip> Labels { get => _labels; private set => SetProperty(ref _labels, value); }
    public IReadOnlyList<DetailSection> Sections { get => _sections; private set => SetProperty(ref _sections, value); }
    public IReadOnlyList<SourceLine> SourceLines { get => _sourceLines; private set => SetProperty(ref _sourceLines, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            SearchResults = _index is null || string.IsNullOrWhiteSpace(value)
                ? []
                : _index.Search(value, 12).Select(o => new XRayRow(o.Id, o.Name, o.Id, null)).ToList();
        }
    }

    public IReadOnlyList<XRayRow> SearchResults { get => _searchResults; private set { if (SetProperty(ref _searchResults, value)) OnPropertyChanged(nameof(HasSearchResults)); } }
    public bool HasSearchResults => _searchResults.Count > 0;

    // ------------------------------------------------------------------ loading

    public async Task LoadAsync()
    {
        try
        {
            var index = await Task.Run(() => CharacterSemanticIndexer.Build(_root, _entry));
            _index = index;
            foreach (var lens in Lenses) lens.Build(index);

            var errors = index.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnings = index.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            Status = $"{index.Objects.Count:N0} objects · {index.Relationships.Count:N0} relationships · {index.Of(ObjectKind.Ability).Count()} abilities" +
                     (warnings + errors > 0 ? $" · {warnings + errors} parse notes" : string.Empty);

            var first = index.Of(ObjectKind.Ability).FirstOrDefault()?.Id
                        ?? index.Of(ObjectKind.State).FirstOrDefault(s => !s.IsStub)?.Id
                        ?? index.Objects.FirstOrDefault()?.Id;
            if (first is not null) Select(first);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "The character could not be read: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ------------------------------------------------------------------ selection

    public void Select(string id)
    {
        var index = _index;
        if (index is null || index.Get(id) is null) return;
        if (!_navigating && (_history.Count == 0 || _history.Peek() != id)) _history.Push(id);
        _selectedId = id;
        if (_searchResults.Count > 0)
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            SearchResults = [];
        }

        var related = Related(index, id);
        foreach (var lens in Lenses) lens.OnSelection(index, id, related);
        ShowDetails(index, id);
        OnPropertyChanged(nameof(SelectedId));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Back()
    {
        if (_history.Count <= 1) return;
        _history.Pop();
        _navigating = true;
        try { Select(_history.Peek()); }
        finally { _navigating = false; }
    }

    /// <summary>Everything to highlight in other lenses when <paramref name="id"/> is selected.</summary>
    private static HashSet<string> Related(SemanticIndex index, string id)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { id };
        void Near(string x) { foreach (var n in index.Neighborhood(x, 1)) set.Add(n); }
        Near(id);

        var obj = index.Get(id)!;
        switch (obj.Kind)
        {
            case ObjectKind.State:
                foreach (var c in index.ControllersOf(id))
                {
                    set.Add(c.Id);
                    Near(c.Id);
                    Near(c.Id + "/hitdef");
                }

                foreach (var r in index.Incoming(id, RelationKind.ChangesState, RelationKind.SetsVictimState, RelationKind.SetsAttackerState, RelationKind.HelperRunsState))
                {
                    var controller = r.From.EndsWith("/hitdef", StringComparison.Ordinal) ? r.From[..^7] : r.From;
                    set.Add(controller);
                    foreach (var cmd in index.Outgoing(controller, RelationKind.ReferencesCommand)) set.Add(cmd.To);
                }

                break;
            case ObjectKind.Controller or ObjectKind.HitDef:
                if (index.OwnerState(id) is { } owner) set.Add(owner.Id);
                Near(id + "/hitdef");
                break;
            case ObjectKind.Ability:
                foreach (var r in index.Incoming(id, RelationKind.PartOf)) set.Add(r.From);
                break;
            case ObjectKind.Variable or ObjectKind.Helper or ObjectKind.Command or ObjectKind.Projectile:
                foreach (var n in index.Neighborhood(id, 2)) set.Add(n);
                break;
        }

        return set;
    }

    // ------------------------------------------------------------------ details + source

    private void ShowDetails(SemanticIndex index, string id)
    {
        var obj = index.Get(id)!;
        Title = obj.Name;
        KindText = obj.Kind + (obj.IsStub ? " · referenced but not defined" : string.Empty);
        IdText = obj.Id;

        Labels = obj.Labels.Select(l => new LabelChip(l.Text, l.Category, ConfidenceStyle.Glyph(l.Confidence),
            ConfidenceStyle.Brush(l.Confidence), $"{ConfidenceStyle.Label(l.Confidence)} · {l.RuleId} · {EvidenceRules.Get(l.RuleId).Description}")).ToList();

        var explanation = index.Explain(id);
        Sections = explanation is null
            ? []
            : explanation.Sections.Select(s => new DetailSection(s.Title, s.Items.Select(i =>
                new XRayRow(i.Id, i.Name, i.Note ?? i.Via, i.Confidence,
                    tooltip: i.Rule is null ? i.Id : $"{ConfidenceStyle.Label(i.Confidence)} · {i.Rule} · {EvidenceRules.Get(i.Rule).Description}")).ToList())).ToList();

        ShowSource(index, obj.Source);
    }

    /// <summary>Shows the file lines around a span, with the span itself marked.</summary>
    public void ShowSource(SemanticIndex index, SourceRef? source)
    {
        _sourceFile = string.Empty;
        SourceText = string.Empty;
        SourceLines = [];
        if (source is { } s && index.FileOf(s) is { } file)
        {
            var path = Path.IsPathRooted(file.RelPath) ? file.RelPath : Path.Combine(_root, file.RelPath.Replace('/', Path.DirectorySeparatorChar));
            _sourceFile = path;
            SourceText = s.StartLine == s.EndLine ? $"{file.RelPath}:{s.StartLine}" : $"{file.RelPath}:{s.StartLine}–{s.EndLine}";
            SourceLines = ReadWindow(path, s);
        }

        OnPropertyChanged(nameof(HasSource));
        CommandManager.InvalidateRequerySuggested();
    }

    public void ShowSourceLine(SourceRef? source)
    {
        if (_index is not null) ShowSource(_index, source);
    }

    private static IReadOnlyList<SourceLine> ReadWindow(string path, SourceRef span)
    {
        try
        {
            var text = Core.Parsing.DefFileReader.ReadFileContent(path);
            if (text is null) return [];
            var lines = SourceLexer.SplitLines(text).ToList();
            var from = Math.Max(1, span.StartLine - 4);
            var to = Math.Min(lines.Count, Math.Min(span.EndLine + 4, span.StartLine + 60));
            var result = new List<SourceLine>();
            for (var n = from; n <= to; n++) result.Add(new SourceLine(n, lines[n - 1], n >= span.StartLine && n <= span.EndLine));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void OpenFile()
    {
        if (_sourceFile.Length == 0 || !File.Exists(_sourceFile)) return;
        try { Process.Start(new ProcessStartInfo(_sourceFile) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UserDialogs.Warn("The file could not be opened: " + ex.Message, "Open source");
        }
    }

    /// <summary>Opens the existing Sprite Inspector on the sprite named by a <c>sprite:group,index</c> id.</summary>
    public void OpenSprite(string spriteId)
    {
        if (!spriteId.StartsWith("sprite:", StringComparison.Ordinal)) return;
        var parts = spriteId["sprite:".Length..].Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var group) || !int.TryParse(parts[1], out var number)) return;

        var sff = CharacterDetailsReader.ResolveSprite(_root, _entry);
        if (sff is null)
        {
            UserDialogs.Warn($"{CharacterName} has no SFF sprite file that IKEMEN can find.", "Sprite Inspector");
            return;
        }

        var window = new SpriteInspectorWindow(new SpriteInspectorViewModel(CharacterName, sff, _root, _entry) { InitialSprite = (group, number) })
        {
            Owner = Application.Current?.Windows.OfType<XRayWindow>().FirstOrDefault()
        };
        window.Show();
    }
}
