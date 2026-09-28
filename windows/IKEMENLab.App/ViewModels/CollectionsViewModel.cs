using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Models;

namespace IKEMENLab.App.ViewModels;

public sealed record CollectionListItem(CharacterCollection? Collection, int Count, CollectionRosterStatus RosterStatus = CollectionRosterStatus.NotActive)
{
    public string Name => Collection?.Name ?? "All Characters";
    public string Detail
    {
        get
        {
            var kind = Collection?.Kind.ToString() ?? "Library";
            var status = RosterStatus switch
            {
                CollectionRosterStatus.Active => "Active",
                CollectionRosterStatus.Modified => "Modified",
                CollectionRosterStatus.CannotActivate => "Cannot Activate",
                _ => "Not Active"
            };
            return $"{kind} / {Count} characters · {status}";
        }
    }

    public bool IsAllCharacters => Collection is null;
}

public sealed class CollectionCharacterRow : ObservableObject
{
    private ImageSource? _thumbnail;
    public required ResolvedMember Member { get; init; }
    public string Name => Member.Character?.DisplayName ?? Member.Member.DisplayName;
    public string Detail => Member.Character is { } c ? $"{c.Author} / {c.Status}" : "Missing from current index";
    public string Path => Member.Member.CharacterId;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1];
    public ImageSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
}

public sealed class CollectionRuleViewModel : ObservableObject
{
    private RuleField _field;
    private RuleComparison _comparison;
    private string _value = "";
    public RuleField Field { get => _field; set => SetProperty(ref _field, value); }
    public RuleComparison Comparison { get => _comparison; set => SetProperty(ref _comparison, value); }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public CollectionRule ToRule() => new(Field, Comparison, Value);
}

public sealed class CollectionsViewModel : ObservableObject
{
    private readonly CollectionStore _store;
    private readonly ICollectionActivationService _activation;
    private readonly ArtworkLoader _artwork;
    private readonly Func<Task> _refreshLibrary;
    private IReadOnlyList<CharacterEntry> _index = [];
    private string? _root;
    private bool _storeReady;
    private bool _selectDefAvailable;
    private CollectionListItem? _selected;
    private string _search = "", _error = "", _editName = "", _statusText = "";
    private bool _adding, _editing, _editSmart, _previewing, _activating, _previewExpanded;
    private RuleMatch _editMatch;
    private Guid? _editId;
    private int _rowGeneration;
    private CollectionActivationPreview? _preview;

    public CollectionsViewModel(
        ArtworkLoader artwork,
        Func<Task>? refreshLibrary = null,
        CollectionStore? store = null,
        ICollectionActivationService? activation = null)
    {
        _artwork = artwork;
        _refreshLibrary = refreshLibrary ?? (() => Task.CompletedTask);
        _store = store ?? new CollectionStore();
        _activation = activation ?? new CollectionActivationService();
        NewCommand = new RelayCommand(() => BeginEdit(false), () => CanCreate);
        NewSmartCommand = new RelayCommand(() => BeginEdit(true), () => CanCreate);
        EditCommand = new RelayCommand(() => BeginEdit(Selected!.Collection!.Kind == CollectionKind.Smart, true), () => CanEdit);
        SaveCommand = new RelayCommand(SaveEditor, () => IsEditing);
        CancelCommand = new RelayCommand(() => IsEditing = false);
        AddRuleCommand = new RelayCommand(() => Rules.Add(new()));
        RemoveRuleCommand = new RelayCommand(p => { if (p is CollectionRuleViewModel rule) Rules.Remove(rule); });
        ToggleAddCommand = new RelayCommand(() => { IsAdding = !IsAdding; SearchText = ""; }, () => CanManageMembers);
        AddMemberCommand = new RelayCommand(p =>
        {
            if (p is CollectionCharacterRow { Member.Character: { } character })
                Change(() => _store.Add(_root!, Selected!.Collection!.Id, [character]));
        }, _ => CanManageMembers && IsAdding);
        RemoveMemberCommand = new RelayCommand(p =>
        {
            if (p is CollectionCharacterRow row)
                Change(() => _store.Remove(_root!, Selected!.Collection!.Id, [row.Path]));
        }, _ => CanManageMembers && !IsAdding);
        BeginActivateCommand = new RelayCommand(BeginActivate, () => CanBeginActivate);
        ConfirmActivateCommand = new AsyncRelayCommand(ConfirmActivateAsync, () => CanConfirmActivate);
        CancelPreviewCommand = new RelayCommand(() => ClearPreview(), () => IsPreviewing && !IsActivating);
        TogglePreviewDetailsCommand = new RelayCommand(() => PreviewExpanded = !PreviewExpanded, () => IsPreviewing);
    }

    public ObservableCollection<CollectionListItem> Collections { get; } = [];
    public ObservableCollection<CollectionCharacterRow> Characters { get; } = [];
    public ObservableCollection<CollectionRuleViewModel> Rules { get; } = [];
    public Array Fields { get; } = Enum.GetValues<RuleField>();
    public Array Comparisons { get; } = Enum.GetValues<RuleComparison>();
    public Array Matches { get; } = Enum.GetValues<RuleMatch>();
    public ICommand NewCommand { get; }
    public ICommand NewSmartCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand RemoveRuleCommand { get; }
    public ICommand ToggleAddCommand { get; }
    public ICommand AddMemberCommand { get; }
    public ICommand RemoveMemberCommand { get; }
    public ICommand BeginActivateCommand { get; }
    public ICommand ConfirmActivateCommand { get; }
    public ICommand CancelPreviewCommand { get; }
    public ICommand TogglePreviewDetailsCommand { get; }

    public bool CanCreate => _root is not null && _storeReady && !IsEditing && !IsPreviewing && !IsActivating;
    public bool CanEdit => CanCreate && Selected?.Collection is not null;
    public bool CanManageMembers => CanEdit && Selected!.Collection!.Kind == CollectionKind.Manual;
    public bool CanRemoveMembers => CanManageMembers && !IsAdding;
    public bool CanBeginActivate =>
        _root is not null && _storeReady && _selectDefAvailable && Selected is not null &&
        !IsEditing && !IsPreviewing && !IsActivating &&
        Selected.RosterStatus != CollectionRosterStatus.CannotActivate;
    public bool CanConfirmActivate =>
        IsPreviewing && !IsActivating && Preview is { CanActivate: true };

    public string Title => Selected?.Name ?? "Collections";
    public string LibraryPath => _root ?? "No installation selected";
    public string Metadata => Selected?.Collection is { } c
        ? $"{c.Kind} / {Selected.Count} characters / Updated {c.ModifiedAtUtc.ToLocalTime():g}"
        : $"Library / {_index.Count} characters";
    public string RuleSummary => Selected?.Collection is { Kind: CollectionKind.Smart } c
        ? $"Match {c.Match.ToString().ToLowerInvariant()}: " + (c.Rules.Count == 0 ? "all characters" :
            string.Join("; ", c.Rules.Select(r => $"{r.Field} {r.Comparison} {r.Value}"))) : "";
    public string ResultsText => $"{Characters.Count} {(IsAdding ? "available" : "shown")}";
    public bool IsEmpty => Characters.Count == 0;
    public string EmptyText => IsAdding ? "No more matching characters" : SearchText.Length > 0 ? "No matches" : "No characters in this collection";
    public string AddLabel => IsAdding ? "Done" : "Add characters";
    public string RosterStatusText => Selected?.RosterStatus switch
    {
        CollectionRosterStatus.Active => "Active",
        CollectionRosterStatus.Modified => "Modified",
        CollectionRosterStatus.CannotActivate => "Cannot Activate",
        _ => "Not Active"
    };
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }
    public bool EditSmart { get => _editSmart; private set => SetProperty(ref _editSmart, value); }
    public RuleMatch EditMatch { get => _editMatch; set => SetProperty(ref _editMatch, value); }

    public CollectionActivationPreview? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(PreviewSummary));
                OnPropertyChanged(nameof(PreviewWarning));
                OnPropertyChanged(nameof(PreviewDetails));
                OnPropertyChanged(nameof(CanConfirmActivate));
            }
        }
    }

    public string PreviewSummary => Preview?.Summary ?? "";
    public string PreviewWarning => Preview?.Warning ?? Preview?.Error ?? "";
    public string PreviewDetails
    {
        get
        {
            if (Preview is null) return "";
            var lines = new List<string>();
            void Add(string label, IReadOnlyList<string> names)
            {
                if (names.Count == 0) return;
                lines.Add($"{label}: {string.Join(", ", names.Take(40))}{(names.Count > 40 ? "…" : "")}");
            }
            Add("Will enable", Preview.WillEnableNames);
            Add("Will disable", Preview.WillDisableNames);
            Add("Already active", Preview.AlreadyActiveNames);
            Add("Missing", Preview.MissingNames);
            Add("Ambiguous", Preview.AmbiguousNames);
            return string.Join(Environment.NewLine, lines);
        }
    }

    public bool IsEditing
    {
        get => _editing;
        private set { if (SetProperty(ref _editing, value)) NotifySelection(); }
    }
    public bool IsAdding
    {
        get => _adding;
        private set { if (SetProperty(ref _adding, value)) { OnPropertyChanged(nameof(AddLabel)); OnPropertyChanged(nameof(CanRemoveMembers)); RefreshRows(); } }
    }
    public bool IsPreviewing
    {
        get => _previewing;
        private set { if (SetProperty(ref _previewing, value)) NotifySelection(); }
    }
    public bool IsActivating
    {
        get => _activating;
        private set { if (SetProperty(ref _activating, value)) NotifySelection(); }
    }
    public bool PreviewExpanded
    {
        get => _previewExpanded;
        set { if (SetProperty(ref _previewExpanded, value)) OnPropertyChanged(nameof(PreviewExpandLabel)); }
    }
    public string PreviewExpandLabel => PreviewExpanded ? "Hide details" : "Show details";

    public string SearchText { get => _search; set { if (SetProperty(ref _search, value)) RefreshRows(); } }
    public CollectionListItem? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            IsEditing = false;
            IsAdding = false;
            ClearPreview();
            SearchText = "";
            NotifySelection();
            RefreshRows();
        }
    }

    public void ApplySnapshot(LibrarySnapshot? snapshot)
    {
        _root = snapshot is { Installation.CanBrowse: true } ? snapshot.Installation.RootPath : null;
        _index = snapshot?.Characters ?? [];
        _selectDefAvailable = snapshot?.SelectDef?.IsAvailable == true;
        IsEditing = false;
        IsAdding = false;
        ClearPreview();
        Reload(Selected?.Collection?.Id, Selected?.IsAllCharacters == true);
        OnPropertyChanged(nameof(LibraryPath));
    }

    public void DeleteSelected()
    {
        if (!CanEdit) return;
        Change(() => _store.Delete(_root!, Selected!.Collection!.Id));
    }

    private void BeginActivate()
    {
        if (_root is null || Selected is null) return;
        Error = "";
        StatusText = "";
        try
        {
            var preview = _activation.Preview(
                _root,
                Selected.Collection,
                _index,
                Selected.IsAllCharacters);
            Preview = preview;
            IsPreviewing = true;
            PreviewExpanded = false;
            if (!preview.CanActivate)
                Error = preview.Error ?? "Cannot activate this collection.";
            NotifySelection();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task ConfirmActivateAsync()
    {
        if (_root is null || Selected is null || Preview is not { CanActivate: true }) return;
        IsActivating = true;
        Error = "";
        StatusText = "Activating collection…";
        try
        {
            var root = _root;
            var collection = Selected.Collection;
            var allChars = Selected.IsAllCharacters;
            var index = _index;
            var result = await Task.Run(() => _activation.Activate(root, collection, index, allChars));
            if (!result.Success)
            {
                Error = result.Error ?? "Activation failed.";
                StatusText = "";
                return;
            }

            ClearPreview();
            StatusText = result.Description ?? "Collection activated.";
            if (!string.IsNullOrWhiteSpace(result.Warning))
                StatusText += " " + result.Warning;
            await _refreshLibrary();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            StatusText = "";
        }
        finally
        {
            IsActivating = false;
            NotifySelection();
        }
    }

    private void ClearPreview()
    {
        IsPreviewing = false;
        Preview = null;
        PreviewExpanded = false;
    }

    private void BeginEdit(bool smart, bool existing = false)
    {
        var c = existing ? Selected?.Collection : null;
        _editId = c?.Id;
        EditName = c?.Name ?? "";
        EditSmart = smart;
        EditMatch = c?.Match ?? RuleMatch.All;
        Rules.Clear();
        foreach (var rule in c?.Rules ?? []) Rules.Add(new() { Field = rule.Field, Comparison = rule.Comparison, Value = rule.Value });
        if (smart && c is null) Rules.Add(new());
        Error = "";
        ClearPreview();
        IsAdding = false;
        IsEditing = true;
    }

    private void SaveEditor()
    {
        try
        {
            var rules = Rules.Select(r => r.ToRule()).ToArray();
            var saved = _editId is { } id ? _store.Edit(_root!, id, EditName, rules, EditMatch)
                : _store.Create(_root!, EditName, EditSmart ? CollectionKind.Smart : CollectionKind.Manual, rules, EditMatch);
            IsEditing = false;
            Reload(saved.Id);
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private void Change(Action action)
    {
        var id = Selected?.Collection?.Id;
        var allChars = Selected?.IsAllCharacters == true;
        var adding = IsAdding;
        var search = SearchText;
        try { action(); Reload(id, allChars); IsAdding = adding && CanManageMembers; SearchText = search; }
        catch (Exception ex) { Error = ex.Message; }
    }

    private void Reload(Guid? selectId, bool preferAllCharacters = false)
    {
        Collections.Clear();
        _storeReady = false;
        try
        {
            CollectionRosterStatus StatusFor(CharacterCollection? c, bool all)
            {
                if (_root is null || !_selectDefAvailable) return CollectionRosterStatus.CannotActivate;
                return _activation.GetRosterStatus(_root, c, _index, all);
            }

            Collections.Add(new(null, _index.Count, StatusFor(null, true)));
            if (_root is not null)
            {
                foreach (var c in _store.Load(_root).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    Collections.Add(new(c, CollectionMembership.Resolve(c, _index).Count, StatusFor(c, false)));
            }
            _storeReady = true;
        }
        catch (Exception ex) { Error = ex.Message; }

        // Assign without going through Selected setter side-effects when possible.
        CollectionListItem? next;
        if (preferAllCharacters)
            next = Collections.FirstOrDefault(c => c.IsAllCharacters) ?? Collections.FirstOrDefault();
        else
            next = Collections.FirstOrDefault(c => c.Collection?.Id == selectId) ?? Collections.FirstOrDefault();

        if (!Equals(_selected, next))
            Selected = next;
        else
        {
            _selected = next;
            OnPropertyChanged(nameof(Selected));
            NotifySelection();
            RefreshRows();
        }
    }

    private void NotifySelection()
    {
        foreach (var name in new[]
                 {
                     nameof(Title), nameof(Metadata), nameof(RuleSummary), nameof(CanCreate), nameof(CanEdit),
                     nameof(CanManageMembers), nameof(CanRemoveMembers), nameof(CanBeginActivate),
                     nameof(CanConfirmActivate), nameof(RosterStatusText), nameof(IsPreviewing), nameof(IsActivating)
                 })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshRows()
    {
        var generation = ++_rowGeneration;
        IEnumerable<ResolvedMember> members = Selected?.Collection is { } c ? CollectionMembership.Resolve(c, _index)
            : _index.Select(e => new ResolvedMember(new(e.Id, e.DisplayName), e));
        if (IsAdding)
        {
            var present = members.Select(m => CollectionMembership.Key(m.Member.CharacterId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            members = _index.Where(e => !present.Contains(CollectionMembership.Key(e.Id)))
                .Select(e => new ResolvedMember(new(e.Id, e.DisplayName), e));
        }
        var query = SearchText.Trim();
        Characters.Clear();
        foreach (var member in members)
        {
            var row = new CollectionCharacterRow { Member = member };
            if (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Detail.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                Characters.Add(row);
        }
        OnPropertyChanged(nameof(ResultsText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        _ = LoadPortraitsAsync(Characters.ToArray(), generation);
    }

    private async Task LoadPortraitsAsync(IEnumerable<CollectionCharacterRow> rows, int generation)
    {
        foreach (var row in rows)
        {
            if (generation != _rowGeneration) return;
            if (row.Member.Character is { } c) row.Thumbnail = await _artwork.CharacterThumbnailAsync(c);
        }
    }
}
