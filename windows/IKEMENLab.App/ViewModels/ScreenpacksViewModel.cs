using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Screenpacks;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;

namespace IKEMENLab.App.ViewModels;

public sealed class ScreenpackRowViewModel : ObservableObject
{
    private ImageSource? _preview;
    private bool _isActivating;

    public ScreenpackRowViewModel(ScreenpackEntry entry, int rosterCells, DateTime nowUtc)
    {
        Entry = entry;
        ComponentsText = string.Join(" · ", entry.ComponentNames());
        AgeText = entry.ModifiedAtUtc is { } m ? RecentContent.FormatRelativeAge(m, nowUtc) : "—";
        if (entry.CharacterSlots > 0 && rosterCells > entry.CharacterSlots)
        {
            CapacityWarning = $"{rosterCells - entry.CharacterSlots} roster entries won't fit ({rosterCells} in select.def, {entry.CharacterSlots} slots)";
        }
    }

    public ScreenpackEntry Entry { get; }
    public string Name => Entry.Name;
    public string Author => Entry.Author;
    public string PathText => Entry.DefPath;
    public string TypeText => Entry.PrimaryType;
    public string ResolutionText => Entry.ResolutionText;
    public string SlotsText => Entry.SlotsText;
    public string ComponentsText { get; }
    public string AgeText { get; }
    public bool IsActive => Entry.IsActive;
    public string? CapacityWarning { get; }
    public bool HasCapacityWarning => CapacityWarning is not null;
    public bool CanActivate => !IsActive && !_isActivating;

    public bool IsActivating
    {
        get => _isActivating;
        set
        {
            if (SetProperty(ref _isActivating, value))
                OnPropertyChanged(nameof(CanActivate));
        }
    }

    public ImageSource? Preview
    {
        get => _preview;
        set => SetProperty(ref _preview, value);
    }

    public bool Matches(string q)
        => Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
           Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
           PathText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
           ComponentsText.Contains(q, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Screenpack (motif) browser with safe Motif activation.</summary>
public sealed class ScreenpacksViewModel : ObservableObject
{
    private readonly ArtworkLoader _artwork;
    private readonly IScreenpackActivationService _activation;
    private readonly Func<Task> _refreshLibrary;
    private readonly List<ScreenpackRowViewModel> _all = [];
    private string _searchText = string.Empty;
    private string _error = "";
    private string _statusText = "";
    private BrowserViewMode _viewMode = BrowserViewMode.List;
    private ScreenpackRowViewModel? _selected;
    private string? _root;
    private bool _previewing, _activating;
    private ScreenpackActivationPreview? _preview;

    public ScreenpacksViewModel(
        ArtworkLoader artwork,
        Action<NavPage> navigate,
        Func<Task>? refreshLibrary = null,
        IScreenpackActivationService? activation = null)
    {
        _artwork = artwork;
        _refreshLibrary = refreshLibrary ?? (() => Task.CompletedTask);
        _activation = activation ?? new ScreenpackActivationService();
        GoHomeCommand = new RelayCommand(() => navigate(NavPage.Dashboard));
        OpenFolderCommand = new RelayCommand(p =>
        {
            if (p is not ScreenpackRowViewModel row || _root is null) return;
            var def = Path.GetFullPath(Path.Combine(_root, row.Entry.DefPath));
            if (File.Exists(def)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{def}\"") { UseShellExecute = true });
        });
        BeginActivateCommand = new RelayCommand(p => BeginActivate(p as ScreenpackRowViewModel ?? Selected), _ => CanBeginActivate);
        ConfirmActivateCommand = new AsyncRelayCommand(_ => ConfirmActivateAsync(), _ => CanConfirmActivate);
        CancelPreviewCommand = new RelayCommand(() => ClearPreview(), () => IsPreviewing && !IsActivating);
    }

    public ObservableCollection<ScreenpackRowViewModel> Screenpacks { get; } = [];
    public ICommand GoHomeCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand BeginActivateCommand { get; }
    public ICommand ConfirmActivateCommand { get; }
    public ICommand CancelPreviewCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplyFilter();
        }
    }

    public BrowserViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
            {
                OnPropertyChanged(nameof(IsListMode));
                OnPropertyChanged(nameof(IsGridMode));
            }
        }
    }

    public bool IsListMode => ViewMode == BrowserViewMode.List;
    public bool IsGridMode => ViewMode == BrowserViewMode.Grid;
    public int VisibleCount => Screenpacks.Count;
    public bool HasNone => _all.Count == 0;
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public ScreenpackActivationPreview? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(PreviewWarningsText));
                OnPropertyChanged(nameof(CanConfirmActivate));
            }
        }
    }

    public string PreviewWarningsText => Preview is null || Preview.Warnings.Count == 0
        ? ""
        : string.Join(Environment.NewLine, Preview.Warnings);

    public bool IsPreviewing
    {
        get => _previewing;
        private set { if (SetProperty(ref _previewing, value)) InvalidateActivate(); }
    }

    public bool IsActivating
    {
        get => _activating;
        private set { if (SetProperty(ref _activating, value)) InvalidateActivate(); }
    }

    public bool CanBeginActivate =>
        _root is not null && Selected is { CanActivate: true } && !IsPreviewing && !IsActivating;

    public bool CanConfirmActivate =>
        IsPreviewing && !IsActivating && Preview is { CanActivate: true };

    public ScreenpackRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            ClearPreview();
            InvalidateActivate();
        }
    }

    public void ApplySnapshot(LibrarySnapshot? snapshot)
    {
        var previousRoot = _root;
        var previousDef = _selected?.Entry.DefPath;
        _all.Clear();
        Screenpacks.Clear();
        ClearPreview();
        _root = snapshot?.Installation.RootPath;
        if (snapshot is not null)
        {
            var cells = snapshot.SelectDef?.Document?.Characters.Count(e => !e.IsCommented && e.Kind != SelectDefEntryKind.Invalid) ?? 0;
            var now = DateTime.UtcNow;
            foreach (var entry in snapshot.Screenpacks) _all.Add(new ScreenpackRowViewModel(entry, cells, now));
        }

        ApplyFilter();
        var sameInstallation = previousRoot is not null && _root is not null &&
                               string.Equals(previousRoot, _root, StringComparison.OrdinalIgnoreCase);
        Selected = BrowserSelection.AfterRefresh(
            Screenpacks, r => r.Entry.DefPath, sameInstallation ? previousDef : null, selectFirstWhenNone: false);
        OnPropertyChanged(nameof(HasNone));
        InvalidateActivate();
        foreach (var row in _all) _ = LoadPreviewAsync(row);
    }

    private void BeginActivate(ScreenpackRowViewModel? row)
    {
        if (row is null || _root is null) return;
        Selected = row;
        Error = "";
        StatusText = "";
        try
        {
            var preview = _activation.Preview(_root, row.Entry);
            Preview = preview;
            IsPreviewing = true;
            if (!preview.CanActivate)
                Error = preview.Error ?? "Cannot activate this screenpack.";
            InvalidateActivate();
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
        Selected.IsActivating = true;
        Error = "";
        StatusText = "Activating screenpack…";
        try
        {
            var root = _root;
            var entry = Selected.Entry;
            var result = await Task.Run(() => _activation.Activate(root, entry));
            if (!result.Success)
            {
                Error = result.Error ?? "Activation failed.";
                StatusText = "";
                return;
            }

            ClearPreview();
            StatusText = result.Description ?? "Screenpack activated.";
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
            if (Selected is not null) Selected.IsActivating = false;
            InvalidateActivate();
        }
    }

    private void ClearPreview()
    {
        IsPreviewing = false;
        Preview = null;
    }

    private void InvalidateActivate()
    {
        OnPropertyChanged(nameof(CanBeginActivate));
        OnPropertyChanged(nameof(CanConfirmActivate));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task LoadPreviewAsync(ScreenpackRowViewModel row)
    {
        var image = await _artwork.ScreenpackPreviewAsync(row.Entry);
        if (_all.Contains(row)) row.Preview = image;
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        Screenpacks.Clear();
        foreach (var row in _all)
        {
            if (q.Length == 0 || row.Matches(q)) Screenpacks.Add(row);
        }

        OnPropertyChanged(nameof(VisibleCount));
    }
}
