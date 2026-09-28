using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;

namespace IKEMENLab.App.ViewModels;

public sealed class StageRowViewModel : ObservableObject
{
    private ImageSource? _preview;
    private bool _canToggleStatus;
    private bool _isToggling;

    public StageRowViewModel(StageEntry entry, DateTime nowUtc)
    {
        Entry = entry;
        AgeText = entry.ModifiedAtUtc is { } modified ? RecentContent.FormatRelativeAge(modified, nowUtc) : "—";
        AgeToolTip = entry.ModifiedAtUtc is { } m ? "DEF modified " + m.ToLocalTime().ToString("g") : "Modification date unavailable";
        DateAddedText = DateAddedSort.FormatAddedLabel(entry.DateAddedUtc, entry.DateAddedSource);
    }

    public StageEntry Entry { get; }
    public string Name => Entry.Name.Trim();
    public string Author => Entry.Author;
    public string PathText => Entry.RootRelativeDefPath;
    public string AgeText { get; }
    public string AgeToolTip { get; }
    /// <summary>"Added Sep 27, 2026" / "Estimated added …" when Date Added is known; otherwise null.</summary>
    public string? DateAddedText { get; }
    public bool HasDateAdded => DateAddedText is not null;
    public bool HasBgm => Entry.HasBgm;
    public bool BgmMissing => Entry.HasBgm && !Entry.BgmFound;
    public string BgmText => HasBgm ? "BGM" : "None";

    public string BgmToolTip => !HasBgm
        ? "No [Music] bgmusic defined"
        : Entry.BgmFound ? "bgmusic = " + Entry.BgmReference : $"bgmusic = {Entry.BgmReference} (file not found)";

    public bool IsActive => Entry.Status == ContentStatus.Active;

    /// <summary>
    /// Re-reads the bound status. A clicked switch shows its new position before the roster write
    /// finishes; if the write fails (or changes nothing) this snaps it back to the real state.
    /// </summary>
    public void RefreshStatusBinding() => OnPropertyChanged(nameof(IsActive));
    public bool IsDisabled => Entry.Status == ContentStatus.Disabled;
    public bool IsUnregistered => Entry.Status == ContentStatus.Unregistered;
    public ContentStatus Status => Entry.Status;

    public bool CanToggleStatus
    {
        get => _canToggleStatus && !_isToggling;
        set
        {
            if (SetProperty(ref _canToggleStatus, value))
                OnPropertyChanged(nameof(StatusToolTip));
        }
    }

    public bool IsToggling
    {
        get => _isToggling;
        set
        {
            if (SetProperty(ref _isToggling, value))
            {
                OnPropertyChanged(nameof(CanToggleStatus));
                OnPropertyChanged(nameof(StatusToolTip));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>macOS: preview 0.6 active, 0.4 disabled, 0.3 unregistered.</summary>
    public double PreviewOpacity => Entry.Status switch
    {
        ContentStatus.Active => 0.85,
        ContentStatus.Disabled => 0.4,
        _ => 0.3
    };

    public string StatusToolTip
    {
        get
        {
            if (IsToggling) return "Updating select.def…";
            if (!CanToggleStatus)
            {
                return Entry.Status switch
                {
                    ContentStatus.Active => "Enabled in select.def (roster toggle unavailable)",
                    ContentStatus.Disabled => "Commented out in select.def (roster toggle unavailable)",
                    _ => "Not in select.def (roster toggle unavailable)"
                };
            }

            return Entry.Status switch
            {
                ContentStatus.Active => "Enabled — click to disable in select.def",
                ContentStatus.Disabled => "Disabled — click to re-enable in select.def",
                _ => "Unregistered — click to add to select.def"
            };
        }
    }

    public string SizeText => Entry.BoundLeft is { } l && Entry.BoundRight is { } r
        ? (r - l) switch
        {
            <= 300 => "Standard",
            <= 600 => "Wide",
            _ => "Extra Wide"
        } + $" · {l}..{r}"
        : "Camera bounds not set";

    public ImageSource? Preview
    {
        get => _preview;
        set => SetProperty(ref _preview, value);
    }

    public bool Matches(string q)
        => Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
           Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
           Entry.DefPath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
           (q.Equals("bgm", StringComparison.OrdinalIgnoreCase) && HasBgm);
}

/// <summary>Stage browser: search, Grid/List, SFF previews, BGM badges, roster toggles.</summary>
public sealed class StagesViewModel : ObservableObject
{
    private readonly ArtworkLoader _artwork;
    private readonly IRosterActivationService _roster;
    private readonly Func<Task> _refreshLibrary;
    private readonly List<StageRowViewModel> _all = [];
    private string _searchText = string.Empty;
    private BrowserSortMode _sortMode = BrowserSortMode.Default;
    private BrowserViewMode _viewMode = BrowserViewMode.List;
    private StageRowViewModel? _selected;
    private string? _root;
    private bool _rosterAvailable;
    private CancellationTokenSource? _loads;

    public StagesViewModel(
        ArtworkLoader artwork,
        Action<NavPage> navigate,
        Func<Task> refreshLibrary,
        IRosterActivationService? roster = null)
    {
        _artwork = artwork;
        _refreshLibrary = refreshLibrary;
        _roster = roster ?? new RosterActivationService();
        GoHomeCommand = new RelayCommand(() => navigate(NavPage.Dashboard));
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as StageRowViewModel ?? Selected));
        CopyPathCommand = new RelayCommand(p => CopyPath(p as StageRowViewModel ?? Selected));
        ToggleStatusCommand = new AsyncRelayCommand(
            p => ToggleStatusAsync(p as StageRowViewModel),
            p => p is StageRowViewModel row && row.CanToggleStatus);
    }

    public ObservableCollection<StageRowViewModel> Stages { get; } = [];
    public ICommand GoHomeCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand ToggleStatusCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplyFilter();
        }
    }

    public BrowserSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value)) ApplyFilter();
        }
    }

    public IReadOnlyList<BrowserSortOption> SortOptions => DateAddedSort.Options;

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
    public int VisibleCount => Stages.Count;
    public bool HasNoResults => _all.Count > 0 && Stages.Count == 0;
    public bool HasNoStages => _all.Count == 0;

    public StageRowViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public void ApplySnapshot(LibrarySnapshot? snapshot)
    {
        _loads?.Cancel();
        _loads = new CancellationTokenSource();
        _all.Clear();
        Stages.Clear();
        _root = snapshot?.Installation.RootPath;
        _rosterAvailable = snapshot is { Installation.CanBrowse: true, SelectDef.IsAvailable: true };
        var now = DateTime.UtcNow;
        if (snapshot is not null)
        {
            foreach (var stage in snapshot.Stages)
            {
                _all.Add(new StageRowViewModel(stage, now) { CanToggleStatus = _rosterAvailable });
            }
        }

        ApplyFilter();
        OnPropertyChanged(nameof(HasNoStages));
        if (snapshot is { Installation.CanBrowse: true }) _ = LoadPreviewsAsync(_all.ToList(), _loads.Token);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Flips the row's select.def status (the switch's command; also used by QA scripts).</summary>
    public async Task ToggleStatusAsync(StageRowViewModel? row)
    {
        if (row is null || _root is null || !row.CanToggleStatus) return;

        var enable = row.Status != ContentStatus.Active;
        row.IsToggling = true;
        try
        {
            var root = _root;
            var defPath = row.Entry.RootRelativeDefPath;
            var result = await Task.Run(() => _roster.SetStageEnabled(root, defPath, enable));
            if (!result.Success)
            {
                UserDialogs.Warn(result.Error ?? "Could not update select.def.", "Roster");
                return;
            }

            await _refreshLibrary();
        }
        catch (Exception ex)
        {
            UserDialogs.Warn(ex.Message, "Roster");
        }
        finally
        {
            row.IsToggling = false;
            row.RefreshStatusBinding();
        }
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        var keep = Selected;
        var filtered = _all.Where(row => q.Length == 0 || row.Matches(q));
        var ordered = DateAddedSort.Apply(
            filtered,
            SortMode,
            row => row.Entry.DateAddedUtc,
            row => row.Name,
            row => row.Entry.DefPath);

        Stages.Clear();
        foreach (var row in ordered) Stages.Add(row);

        if (keep is not null && !Stages.Contains(keep)) Selected = Stages.FirstOrDefault();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasNoResults));
    }

    private async Task LoadPreviewsAsync(IReadOnlyList<StageRowViewModel> rows, CancellationToken token)
    {
        await Task.WhenAll(rows.Select(async row =>
        {
            var image = await _artwork.StageThumbnailAsync(row.Entry);
            if (!token.IsCancellationRequested) row.Preview = image;
        }));
    }

    private void OpenFolder(StageRowViewModel? row)
    {
        if (row is null || _root is null) return;
        var def = Path.GetFullPath(Path.Combine(_root, row.Entry.RootRelativeDefPath));
        if (File.Exists(def)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{def}\"") { UseShellExecute = true });
    }

    private void CopyPath(StageRowViewModel? row)
    {
        if (row is null || _root is null) return;
        try { Clipboard.SetText(Path.GetFullPath(Path.Combine(_root, row.Entry.RootRelativeDefPath))); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }
}
