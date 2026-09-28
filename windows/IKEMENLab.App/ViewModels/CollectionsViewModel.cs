using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.Core.Collections;
using IKEMENLab.Core.Models;

namespace IKEMENLab.App.ViewModels;

public sealed record CollectionListItem(CharacterCollection? Collection, int Count)
{
    public string Name => Collection?.Name ?? "All Characters";
    public string Detail => $"{Collection?.Kind.ToString() ?? "Library"} / {Count} characters";
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
    private readonly ArtworkLoader _artwork;
    private IReadOnlyList<CharacterEntry> _index = [];
    private string? _root;
    private bool _storeReady;
    private CollectionListItem? _selected;
    private string _search = "", _error = "", _editName = "";
    private bool _adding, _editing, _editSmart;
    private RuleMatch _editMatch;
    private Guid? _editId;
    private int _rowGeneration;

    public CollectionsViewModel(ArtworkLoader artwork, CollectionStore? store = null)
    {
        _artwork = artwork;
        _store = store ?? new CollectionStore();
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
    public bool CanCreate => _root is not null && _storeReady && !IsEditing;
    public bool CanEdit => CanCreate && Selected?.Collection is not null;
    public bool CanManageMembers => CanEdit && Selected!.Collection!.Kind == CollectionKind.Manual;
    public bool CanRemoveMembers => CanManageMembers && !IsAdding;
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
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }
    public bool EditSmart { get => _editSmart; private set => SetProperty(ref _editSmart, value); }
    public RuleMatch EditMatch { get => _editMatch; set => SetProperty(ref _editMatch, value); }
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
    public string SearchText { get => _search; set { if (SetProperty(ref _search, value)) RefreshRows(); } }
    public CollectionListItem? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            IsEditing = false;
            IsAdding = false;
            SearchText = "";
            NotifySelection();
            RefreshRows();
        }
    }

    public void ApplySnapshot(LibrarySnapshot? snapshot)
    {
        _root = snapshot is { Installation.CanBrowse: true } ? snapshot.Installation.RootPath : null;
        _index = snapshot?.Characters ?? [];
        IsEditing = false;
        IsAdding = false;
        Reload(Selected?.Collection?.Id);
        OnPropertyChanged(nameof(LibraryPath));
    }

    public void DeleteSelected()
    {
        if (!CanEdit) return;
        Change(() => _store.Delete(_root!, Selected!.Collection!.Id));
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
        var adding = IsAdding;
        var search = SearchText;
        try { action(); Reload(id); IsAdding = adding && CanManageMembers; SearchText = search; }
        catch (Exception ex) { Error = ex.Message; }
    }

    private void Reload(Guid? selectId)
    {
        Collections.Clear();
        Collections.Add(new(null, _index.Count));
        Error = "";
        _storeReady = false;
        try
        {
            if (_root is not null)
                foreach (var c in _store.Load(_root).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    Collections.Add(new(c, CollectionMembership.Resolve(c, _index).Count));
            _storeReady = true;
        }
        catch (Exception ex) { Error = ex.Message; }
        Selected = Collections.FirstOrDefault(c => c.Collection?.Id == selectId) ?? Collections[0];
        NotifySelection();
    }

    private void NotifySelection()
    {
        foreach (var name in new[] { nameof(Title), nameof(Metadata), nameof(RuleSummary), nameof(CanCreate), nameof(CanEdit), nameof(CanManageMembers), nameof(CanRemoveMembers) })
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
