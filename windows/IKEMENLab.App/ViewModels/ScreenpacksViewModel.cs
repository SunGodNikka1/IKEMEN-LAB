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

/// <summary>Read-only screenpack (motif) browser. Activation is a later safe-write phase.</summary>
public sealed class ScreenpacksViewModel : ObservableObject
{
    private readonly ArtworkLoader _artwork;
    private readonly List<ScreenpackRowViewModel> _all = [];
    private string _searchText = string.Empty;
    private BrowserViewMode _viewMode = BrowserViewMode.List;
    private ScreenpackRowViewModel? _selected;
    private string? _root;

    public ScreenpacksViewModel(ArtworkLoader artwork, Action<NavPage> navigate)
    {
        _artwork = artwork;
        GoHomeCommand = new RelayCommand(() => navigate(NavPage.Dashboard));
        OpenFolderCommand = new RelayCommand(p =>
        {
            if (p is not ScreenpackRowViewModel row || _root is null) return;
            var def = Path.GetFullPath(Path.Combine(_root, row.Entry.DefPath));
            if (File.Exists(def)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{def}\"") { UseShellExecute = true });
        });
    }

    public ObservableCollection<ScreenpackRowViewModel> Screenpacks { get; } = [];
    public ICommand GoHomeCommand { get; }
    public ICommand OpenFolderCommand { get; }

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

    public ScreenpackRowViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public void ApplySnapshot(LibrarySnapshot? snapshot)
    {
        _all.Clear();
        Screenpacks.Clear();
        _root = snapshot?.Installation.RootPath;
        if (snapshot is not null)
        {
            var cells = snapshot.SelectDef?.Document?.Characters.Count(e => !e.IsCommented && e.Kind != SelectDefEntryKind.Invalid) ?? 0;
            var now = DateTime.UtcNow;
            foreach (var entry in snapshot.Screenpacks) _all.Add(new ScreenpackRowViewModel(entry, cells, now));
        }

        ApplyFilter();
        OnPropertyChanged(nameof(HasNone));
        foreach (var row in _all) _ = LoadPreviewAsync(row);
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
