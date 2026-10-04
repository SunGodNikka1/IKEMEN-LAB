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
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Names;
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

    public XRayViewModel(string root, CharacterEntry entry, string displayName, ISettingsStore? settings = null, ComboPlaybackService? playback = null,
        NameOverlayStore? names = null)
    {
        NameStore = names ?? NameOverlayStore.CreateDefault();
        _root = root;
        _entry = entry;
        Settings = settings ?? new JsonSettingsStore();
        PlaybackService = playback ?? new ComboPlaybackService();
        // One session for the whole window: Play Combo, Play Ability and Preview State share it, so only one engine runs at a time.
        // Notifications are posted to the UI thread, never invoked synchronously (a worker that blocks on the UI thread could deadlock a close).
        var ui = new UiDispatcher();
        PlaybackSession = new PlaybackSession(PlaybackService, a => ui.Post(a));
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
        RenameCommand = new RelayCommand(Rename, () => CanRenameSelected);
        ResetNameCommand = new RelayCommand(ResetName, () => _selectedId is not null && CharacterNames?.Names.Records.ContainsKey(_selectedId) == true);
        KeepNameCommand = new RelayCommand(p => { if (p is NameReviewRow r) ApplyNameChange(CharacterNames?.Confirm(r.Id)); });
        DiscardNameCommand = new RelayCommand(p => { if (p is NameReviewRow r) ApplyNameChange(CharacterNames?.Clear(r.Id)); });
        AttachNameCommand = new RelayCommand(p =>
        {
            if (p is NameReviewRow r && _selectedId is not null) ApplyNameChange(CharacterNames?.Reattach(r.Id, _selectedId));
        });
        CopyIdCommand = new RelayCommand(() =>
        {
            try { if (_selectedId is not null) Clipboard.SetText(_selectedId); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
        });
    }

    public string CharacterName { get; }
    public string Root => _root;
    public CharacterEntry Entry => _entry;
    public ISettingsStore Settings { get; }

    /// <summary>The cleanup started by <see cref="Close"/> (completed when there was nothing to stop).</summary>
    public Task<ShutdownResult> ShutdownTask { get; private set; } = Task.FromResult(new ShutdownResult(true, null, []));

    /// <summary>
    /// Called when the window closes. Never blocks: it cancels any playback (engine killed, sandbox deleted, no record unless the result was already
    /// committed) on the worker's own time. If that cleanup times out, fails, or leaves a sandbox behind, the user is told once the cleanup ends.
    /// </summary>
    public void Close()
    {
        var ui = new UiDispatcher();
        var task = Combos.Playback.ShutdownAsync();
        ShutdownTask = task;
        PlaybackShutdowns.Track(task);
        _ = task.ContinueWith(t =>
        {
            var problem = t.IsFaulted ? t.Exception!.GetBaseException().Message : t.Result.Clean ? null : string.Join(" ", new[] { t.Result.Problem }.Concat(t.Result.LeftoverSandboxes.Select(l => $"Sandbox left behind (safe to delete): {l}")).Where(x => !string.IsNullOrEmpty(x)));
            if (problem is not null) ui.Post(() => UserDialogs.Warn("Closing X-Ray left a playback unfinished. " + problem, "Combo playback"));
        }, TaskScheduler.Default);
    }
    public ComboPlaybackService PlaybackService { get; }
    /// <summary>The one playback session of this window (Play Combo, Play Ability, Preview State).</summary>
    public PlaybackSession PlaybackSession { get; }

    /// <summary>The character's folder under chars/ and its DEF relative to chars/, as the sandbox wants them.</summary>
    public string SubjectFolder => _entry.FolderPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? _entry.FolderPath["chars/".Length..] : _entry.FolderPath;
    public string SubjectDef => _entry.DefPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? _entry.DefPath["chars/".Length..] : _entry.DefPath;
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
    public ICommand RenameCommand { get; }
    public ICommand ResetNameCommand { get; }
    public ICommand KeepNameCommand { get; }
    public ICommand DiscardNameCommand { get; }
    public ICommand AttachNameCommand { get; }

    // ------------------------------------------------------------------ names

    public NameOverlayStore NameStore { get; }

    /// <summary>The character's saved names, attached to the index (null until loaded).</summary>
    public CharacterNames? CharacterNames { get; private set; }

    private string _renameText = string.Empty;
    private string _nameInfo = string.Empty;
    private string _nameError = string.Empty;
    private IReadOnlyList<NameReviewRow> _nameReview = [];

    /// <summary>The name being typed for the selected object.</summary>
    public string RenameText { get => _renameText; set { if (SetProperty(ref _renameText, value)) NameError = string.Empty; } }

    /// <summary>Where the selected object's label comes from.</summary>
    public string NameInfo { get => _nameInfo; private set => SetProperty(ref _nameInfo, value); }

    public string NameError { get => _nameError; private set { if (SetProperty(ref _nameError, value)) OnPropertyChanged(nameof(HasNameError)); } }
    public bool HasNameError => _nameError.Length > 0;

    public bool CanRenameSelected => _selectedId is not null && CharacterNames?.Names.CanRename(_selectedId) == true;

    /// <summary>Saved names that are not shown because their object changed or no longer exists.</summary>
    public IReadOnlyList<NameReviewRow> NameReview
    {
        get => _nameReview;
        private set
        {
            if (!SetProperty(ref _nameReview, value)) return;
            OnPropertyChanged(nameof(HasNameReview));
            OnPropertyChanged(nameof(NameReviewHeader));
        }
    }

    public bool HasNameReview => _nameReview.Count > 0;
    public string NameReviewHeader => $"Names to review ({_nameReview.Count}): not shown until you decide";

    private void Rename()
    {
        if (_selectedId is null || CharacterNames is null) return;
        ApplyNameChange(CharacterNames.Rename(_selectedId, RenameText));
    }

    private void ResetName()
    {
        if (_selectedId is null || CharacterNames is null) return;
        ApplyNameChange(CharacterNames.Clear(_selectedId));
    }

    /// <summary>After a saved change every lens is rebuilt so the new label shows everywhere at once.</summary>
    private void ApplyNameChange(string? problem)
    {
        if (problem is not null)
        {
            NameError = problem;
            return;
        }

        NameError = string.Empty;
        if (_index is null) return;
        foreach (var lens in Lenses) lens.RefreshNames(_index);
        RefreshNameReview();
        if (_selectedId is not null)
        {
            _navigating = true;
            try { Select(_selectedId); }
            finally { _navigating = false; }
        }
    }

    private void RefreshNameReview() =>
        NameReview = CharacterNames?.Names.Review().Select(r => new NameReviewRow(r)).ToList() ?? [];

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
                : _index.Search(value, 12).Select(o => new XRayRow(o.Id, _index.NameOf(o.Id), o.Id, null)).ToList();
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
            string? namesProblem = null;
            try
            {
                CharacterNames = CharacterNames.Load(NameStore, index, Path.Combine(_root, _entry.FolderPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Names are presentation only: X-Ray keeps working with its own names if the store cannot be read.
                namesProblem = "Your names could not be read: " + ex.Message;
            }

            RefreshNameReview();
            foreach (var lens in Lenses) lens.Build(index);

            var errors = index.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnings = index.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            Status = $"{index.Objects.Count:N0} objects · {index.Relationships.Count:N0} relationships · {index.Of(ObjectKind.Ability).Count()} abilities" +
                     (warnings + errors > 0 ? $" · {warnings + errors} parse notes" : string.Empty) +
                     (namesProblem is null ? string.Empty : " · " + namesProblem);

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
        Title = index.NameOf(id);
        KindText = obj.Kind + (obj.IsStub ? " · referenced but not defined" : string.Empty);
        IdText = index.Names.IsRenamed(id) ? $"{obj.Id} · X-Ray name: {obj.Name}" : obj.Id;
        var source = index.Names.SourceOf(id);
        NameInfo = source switch
        {
            NameSource.User => "Your name",
            NameSource.Linked => "Your name for the linked ability / entry state",
            _ => index.Names.StatusOf(id) is { } st && st != NameStatus.Current
                ? "Your saved name for this object is waiting for review (it changed): see Names to review"
                : "X-Ray name"
        };
        _renameText = index.Names.Records.TryGetValue(id, out var rec) && index.Names.StatusOf(id) == NameStatus.Current ? rec.Label : string.Empty;
        OnPropertyChanged(nameof(RenameText));
        OnPropertyChanged(nameof(CanRenameSelected));

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

/// <summary>One saved name that is not applied, for the review list.</summary>
public sealed class NameReviewRow
{
    public NameReviewRow(NameReviewItem item)
    {
        Id = item.Id;
        Label = item.Label;
        Status = item.Status == NameStatus.Stale
            ? $"{item.CurrentDefaultName ?? item.Id} changed since you named it"
            : $"{item.DefaultNameAtRename} ({item.Id}) no longer exists";
        Suggestions = item.Suggestions.Count == 0 ? string.Empty : "Unchanged match: " + string.Join(", ", item.Suggestions);
        CanKeep = item.Status == NameStatus.Stale;
    }

    public string Id { get; }
    public string Label { get; }
    public string Status { get; }
    public string Suggestions { get; }
    public bool HasSuggestions => Suggestions.Length > 0;
    public bool CanKeep { get; }
}
