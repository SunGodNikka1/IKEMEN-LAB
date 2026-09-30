using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Models;

namespace IKEMENLab.App.ViewModels;

/// <summary>One editable CNS value in the editor.</summary>
public sealed class TuningRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _text;
    private string? _error;

    public TuningRowViewModel(TuningValue value, Action changed)
    {
        Field = value.Field;
        _changed = changed;
        Original = CnsEditor.FormatNumber(value.Current, Field.IsInteger && Math.Abs(value.Current - Math.Round(value.Current)) < 1e-9);
        _text = Original;
        IsEngineDefault = value.IsEngineDefault;
    }

    public TuningField Field { get; }
    public string Label => Field.Label;
    public string Key => Field.Key;
    public string Original { get; }
    public bool IsEngineDefault { get; }

    public string HelpText => IsEngineDefault ? Field.Help + " Not set in the CNS; the engine default is used." : Field.Help;
    public string RangeText => $"{CnsEditor.FormatNumber(Field.Min, Field.IsInteger)} – {CnsEditor.FormatNumber(Field.Max, Field.IsInteger)}";
    public string DefaultHint => IsEngineDefault ? "default" : string.Empty;

    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value)) return;
            OnPropertyChanged(nameof(IsChanged));
            _changed();
        }
    }

    public bool IsChanged => !string.Equals(_text.Trim(), Original, StringComparison.Ordinal);

    public string? Error
    {
        get => _error;
        set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);
}

/// <summary>Edit a character's CNS life/power/attack/defence and [Size] values; Save writes only what changed.</summary>
public sealed class CharacterTuningViewModel : ObservableObject
{
    private readonly string _root;
    private readonly CharacterEntry _character;
    private readonly CharacterTuningService _service;
    private TuningSnapshot? _snapshot;
    private string _status = string.Empty;
    private string? _lastOperation;
    private bool _isBusy;
    private bool _canSave;

    public CharacterTuningViewModel(string root, CharacterEntry character, string displayName, CharacterTuningService? service = null)
    {
        _root = root;
        _character = character;
        _service = service ?? new CharacterTuningService();
        Title = displayName;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => _canSave && !_isBusy);
        ResetCommand = new RelayCommand(Reload, () => !_isBusy);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => _lastOperation is not null && !_isBusy);
        ScaleCommand = new RelayCommand(p => Scale(p as string), _ => HasSnapshot);
        Reload();
    }

    public string Title { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ScaleCommand { get; }

    /// <summary>Raised after a save or undo so the inspector can refresh its bars.</summary>
    public event Action? Changed;

    public ObservableCollection<TuningRowViewModel> StatRows { get; } = [];
    public ObservableCollection<TuningRowViewModel> SizeRows { get; } = [];

    public bool HasSnapshot => _snapshot is not null;
    public string CnsFile => _snapshot is null ? string.Empty : System.IO.Path.GetFileName(_snapshot.CnsPath);
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool CanUndo => _lastOperation is not null;

    public string Intro => HasSnapshot
        ? $"Edits {CnsFile}. Only the lines you change are rewritten; a backup is kept and Undo puts the file back exactly."
        : "This character has no readable CNS file, so its size and stats cannot be edited.";

    public string ScaleHint => "Size multiplies Width and Height scale together. It resizes the sprites and their collision boxes; the other Size values are left alone.";

    private IEnumerable<TuningRowViewModel> AllRows => StatRows.Concat(SizeRows);

    private void Reload()
    {
        StatRows.Clear();
        SizeRows.Clear();
        try { _snapshot = _service.Load(_root, _character); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { _snapshot = null; }

        if (_snapshot is not null)
        {
            foreach (var value in _snapshot.Values)
            {
                var row = new TuningRowViewModel(value, Revalidate);
                (value.Field.Group == TuningFields.Stats ? StatRows : SizeRows).Add(row);
            }
        }

        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(CnsFile));
        OnPropertyChanged(nameof(Intro));
        Revalidate();
    }

    private IReadOnlyList<TuningRequest> Requests() =>
        AllRows.Where(r => r.IsChanged).Select(r => new TuningRequest(r.Field.Id, r.Text.Trim())).ToList();

    private void Revalidate()
    {
        if (_snapshot is null) { _canSave = false; return; }
        var plan = _service.Plan(_snapshot, Requests());
        foreach (var row in AllRows)
            row.Error = plan.Errors.TryGetValue(row.Field.Id, out var message) ? message : null;
        _canSave = plan.CanApply;
        Status = plan.Errors.Count > 0 ? "Fix the highlighted values to save."
            : plan.Changes.Count > 0 ? $"{plan.Changes.Count} change(s) ready to save."
            : _lastOperation is not null ? Status : string.Empty;
        CommandManager.InvalidateRequerySuggested();
    }

    private void Scale(string? factorText)
    {
        if (_snapshot is null || !double.TryParse(factorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)) return;
        foreach (var request in CharacterTuningService.ScaleSize(_snapshot, factor))
        {
            var row = SizeRows.FirstOrDefault(r => r.Field.Id == request.FieldId);
            if (row is not null) row.Text = request.NewValue;
        }
    }

    private async Task SaveAsync()
    {
        if (_snapshot is null) return;
        var snapshot = _snapshot;
        var requests = Requests();
        _isBusy = true;
        try
        {
            var result = await Task.Run(() => _service.Apply(_root, snapshot, requests));
            if (!result.Success)
            {
                Status = result.Error ?? "The change could not be saved.";
                return;
            }

            _lastOperation = result.OperationId ?? _lastOperation;
            Reload();
            OnPropertyChanged(nameof(CanUndo));
            Status = $"Saved {result.Changes.Count} change(s) to {CnsFile}. Undo restores the previous file.";
            Changed?.Invoke();
        }
        finally
        {
            _isBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task UndoAsync()
    {
        if (_lastOperation is not { } operation) return;
        _isBusy = true;
        try
        {
            var result = await Task.Run(() => _service.Undo(operation));
            if (!result.Success)
            {
                Status = result.Error ?? "The change could not be undone.";
                return;
            }

            _lastOperation = null;
            Reload();
            OnPropertyChanged(nameof(CanUndo));
            Status = "Restored the CNS as it was before the last save.";
            Changed?.Invoke();
        }
        finally
        {
            _isBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
