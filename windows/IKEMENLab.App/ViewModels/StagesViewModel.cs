using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Services;

namespace IKEMENLab.App.ViewModels;

public sealed class StageRowViewModel : ObservableObject
{
    private ImageSource? _preview;

    public StageRowViewModel(StageEntry entry, DateTime nowUtc)
    {
        Entry = entry;
        AgeText = entry.ModifiedAtUtc is { } modified ? RecentContent.FormatRelativeAge(modified, nowUtc) : "—";
        AgeToolTip = entry.ModifiedAtUtc is { } m ? "DEF modified " + m.ToLocalTime().ToString("g") : "Modification date unavailable";
    }

    public StageEntry Entry { get; }
    public string Name => Entry.Name.Trim();
    public string Author => Entry.Author;
    public string PathText => Entry.RootRelativeDefPath;
    public string AgeText { get; }
    public string AgeToolTip { get; }
    public bool HasBgm => Entry.HasBgm;
    public bool BgmMissing => Entry.HasBgm && !Entry.BgmFound;
    public string BgmText => HasBgm ? "BGM" : "None";

    public string BgmToolTip => !HasBgm
        ? "No [Music] bgmusic defined"
        : Entry.BgmFound ? "bgmusic = " + Entry.BgmReference : $"bgmusic = {Entry.BgmReference} (file not found)";

    public bool IsActive => Entry.Status == ContentStatus.Active;
    public bool IsDisabled => Entry.Status == ContentStatus.Disabled;
    public bool IsUnregistered => Entry.Status == ContentStatus.Unregistered;

    /// <summary>macOS: preview 0.6 active, 0.4 disabled, 0.3 unregistered.</summary>
    public double PreviewOpacity => Entry.Status switch
    {
        ContentStatus.Active => 0.85,
        ContentStatus.Disabled => 0.4,
        _ => 0.3
    };

    public string StatusToolTip => Entry.Status switch
    {
        ContentStatus.Active => "Enabled in select.def [ExtraStages] (read-only view)",
        ContentStatus.Disabled => "Commented out in select.def (read-only view)",
        _ => "Not in select.def — won't appear in game"
    };

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

/// <summary>Stage browser: search, Grid/List, SFF previews, BGM badges, select.def status (read-only).</summary>
public sealed class StagesViewModel : ObservableObject
{
    private readonly ArtworkLoader _artwork;
    private readonly List<StageRowViewModel> _all = [];
    private string _searchText = string.Empty;
    private BrowserViewMode _viewMode = BrowserViewMode.List;
    private StageRowViewModel? _selected;
    private string? _root;
    private CancellationTokenSource? _loads;

    public StagesViewModel(ArtworkLoader artwork, Action<NavPage> navigate)
    {
        _artwork = artwork;
        GoHomeCommand = new RelayCommand(() => navigate(NavPage.Dashboard));
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as StageRowViewModel ?? Selected));
        CopyPathCommand = new RelayCommand(p => CopyPath(p as StageRowViewModel ?? Selected));
    }

    public ObservableCollection<StageRowViewModel> Stages { get; } = [];
    public ICommand GoHomeCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyPathCommand { get; }

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
        var now = DateTime.UtcNow;
        if (snapshot is not null)
        {
            foreach (var stage in snapshot.Stages) _all.Add(new StageRowViewModel(stage, now));
        }

        ApplyFilter();
        OnPropertyChanged(nameof(HasNoStages));
        if (snapshot is { Installation.CanBrowse: true }) _ = LoadPreviewsAsync(_all.ToList(), _loads.Token);
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        Stages.Clear();
        foreach (var row in _all)
        {
            if (q.Length == 0 || row.Matches(q)) Stages.Add(row);
        }

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
