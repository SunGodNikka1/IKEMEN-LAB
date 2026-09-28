using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Services;

namespace IKEMENLab.App.ViewModels;

public enum BrowserViewMode
{
    Grid,
    List
}

/// <summary>Character browser: search, Grid/List, portraits, feature badges and roster toggles.</summary>
public sealed class CharactersViewModel : ObservableObject
{
    private readonly ArtworkLoader _artwork;
    private readonly Action<NavPage> _navigate;
    private readonly IRosterActivationService _roster;
    private readonly Func<Task> _refreshLibrary;
    private readonly PrimaryDefStore? _primaryDefs;
    private readonly ICharacterDeletionService _deletion;
    private readonly List<CharacterRowViewModel> _all = [];
    private string _searchText = string.Empty;
    private BrowserSortMode _sortMode = BrowserSortMode.Default;
    private BrowserViewMode _viewMode = BrowserViewMode.List;
    private CharacterRowViewModel? _selected;
    private CharacterInspectorViewModel? _inspector;
    private string? _root;
    private bool _rosterAvailable;
    private CancellationTokenSource? _scan;
    private bool _isDeleting;

    public CharactersViewModel(
        ArtworkLoader artwork,
        Action<NavPage> navigate,
        Func<Task> refreshLibrary,
        IRosterActivationService? roster = null,
        PrimaryDefStore? primaryDefs = null,
        ICharacterDeletionService? deletion = null)
    {
        _artwork = artwork;
        _navigate = navigate;
        _refreshLibrary = refreshLibrary;
        _roster = roster ?? new RosterActivationService();
        _primaryDefs = primaryDefs;
        _deletion = deletion ?? new CharacterDeletionService(primaryDefs: primaryDefs);
        GoHomeCommand = new RelayCommand(() => _navigate(NavPage.Dashboard));
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as CharacterRowViewModel ?? Selected));
        CopyPathCommand = new RelayCommand(p => CopyPath(p as CharacterRowViewModel ?? Selected));
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        ToggleStatusCommand = new AsyncRelayCommand(
            p => ToggleStatusAsync(p as CharacterRowViewModel),
            p => p is CharacterRowViewModel row && row.CanToggleStatus);
        DeleteCharacterCommand = new AsyncRelayCommand(
            p => DeleteCharacterAsync(p as CharacterRowViewModel ?? Selected),
            p => CanDelete(p as CharacterRowViewModel ?? Selected));
    }

    public ObservableCollection<CharacterRowViewModel> Characters { get; } = [];

    public ICommand GoHomeCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleStatusCommand { get; }
    public ICommand DeleteCharacterCommand { get; }

    /// <summary>True while a Delete Character operation is running (only one at a time).</summary>
    public bool IsDeleting
    {
        get => _isDeleting;
        private set
        {
            if (SetProperty(ref _isDeleting, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

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
            if (!SetProperty(ref _sortMode, value)) return;
            foreach (var row in _all) row.ShowDateAdded = value != BrowserSortMode.Default;
            ApplyFilter();
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

    public int TotalCount => _all.Count;
    public int VisibleCount => Characters.Count;
    public bool HasNoResults => _all.Count > 0 && Characters.Count == 0;
    public bool HasNoCharacters => _all.Count == 0;

    public CharacterRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) _ = LoadInspectorAsync(value);
        }
    }

    public CharacterInspectorViewModel? Inspector
    {
        get => _inspector;
        private set => SetProperty(ref _inspector, value);
    }

    public void ApplySnapshot(LibrarySnapshot? snapshot)
    {
        // A rebuild creates new rows: remember the selection by id so a Refresh (or a toggle/delete that
        // refreshes) keeps it while the character still exists, and clears it when it is gone.
        var previousRoot = _root;
        var previousId = _selected?.Entry.Id;

        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        _all.Clear();
        Characters.Clear();
        Selected = null;
        _root = snapshot?.Installation.RootPath;
        var sameInstallation = previousRoot is not null && _root is not null &&
                               string.Equals(previousRoot, _root, StringComparison.OrdinalIgnoreCase);
        _rosterAvailable = snapshot is { Installation.CanBrowse: true, SelectDef.IsAvailable: true };

        if (snapshot is not null)
        {
            foreach (var entry in snapshot.Characters)
            {
                _all.Add(new CharacterRowViewModel(entry)
                {
                    CanToggleStatus = _rosterAvailable && !entry.NeedsDefChoice,
                    ShowDateAdded = SortMode != BrowserSortMode.Default
                });
            }
        }

        ApplyFilter();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasNoCharacters));
        if (snapshot is { Installation.CanBrowse: true })
        {
            _ = LoadThumbnailsAsync(_all.ToList(), _scan.Token);
            _ = ScanFeaturesAsync(snapshot.Installation.RootPath, _all.ToList(), _scan.Token);
        }

        Selected = BrowserSelection.AfterRefresh(
            Characters, r => r.Entry.Id,
            sameInstallation ? previousId : null,
            selectFirstWhenNone: !sameInstallation);
        CommandManager.InvalidateRequerySuggested();
    }

    private bool CanDelete(CharacterRowViewModel? row) => row is not null && _root is not null && !IsDeleting;

    /// <summary>
    /// Delete Character: shows what will be removed (folder and select.def lines), asks for confirmation,
    /// deletes through <see cref="ICharacterDeletionService"/> and refreshes the library. The selection
    /// moves to the neighbouring character. Also used by QA scripts.
    /// </summary>
    public async Task<CharacterDeletionResult?> DeleteCharacterAsync(CharacterRowViewModel? row)
    {
        if (row is null || _root is null || IsDeleting) return null;
        var root = _root;
        IsDeleting = true;
        row.IsToggling = true;
        try
        {
            var plan = await Task.Run(() => _deletion.Preview(root, row.Entry));
            if (!plan.CanDelete)
            {
                UserDialogs.Warn(plan.Error ?? "This character cannot be deleted.", "Delete Character");
                return null;
            }

            if (!UserDialogs.Confirm(plan.ConfirmationMessage(), "Delete Character")) return null;

            var adjacent = BrowserSelection.AdjacentId(Characters, r => r.Entry.Id, row.Entry.Id);
            var result = await Task.Run(() => _deletion.Delete(root, row.Entry));
            if (result.Success)
            {
                // Take the row out right away and select its neighbour; the refresh below rebuilds the
                // list (and the Dashboard) from disk and keeps that selection.
                _all.Remove(row);
                Characters.Remove(row);
                Selected = adjacent is null
                    ? null
                    : Characters.FirstOrDefault(r => string.Equals(r.Entry.Id, adjacent, StringComparison.OrdinalIgnoreCase));
                OnPropertyChanged(nameof(TotalCount));
                OnPropertyChanged(nameof(VisibleCount));
                OnPropertyChanged(nameof(HasNoCharacters));
                OnPropertyChanged(nameof(HasNoResults));
            }

            // Refresh either way: after a failure it shows the installation exactly as it is now.
            await _refreshLibrary();

            if (!result.Success)
            {
                UserDialogs.Warn(result.Error ?? "The character could not be deleted.", "Delete Character");
            }
            else if (result.Warnings.Count > 0)
            {
                UserDialogs.Warn($"\"{plan.DisplayName}\" was deleted, with a note:\n\n" + string.Join("\n", result.Warnings),
                    "Delete Character");
            }

            return result;
        }
        catch (Exception ex)
        {
            UserDialogs.Warn(ex.Message, "Delete Character");
            return null;
        }
        finally
        {
            row.IsToggling = false;
            IsDeleting = false;
        }
    }

    /// <summary>Flips the row's select.def status (the switch's command; also used by QA scripts).</summary>
    public async Task ToggleStatusAsync(CharacterRowViewModel? row)
    {
        if (row is null || _root is null || !row.CanToggleStatus) return;
        if (row.Entry.NeedsDefChoice)
        {
            UserDialogs.Warn(
                "This character folder has several possible DEFs. Choose the primary DEF in the inspector first.",
                "Roster");
            return;
        }

        var enable = row.Status != ContentStatus.Active;
        row.IsToggling = true;
        try
        {
            var root = _root;
            var defPath = row.Entry.DefPath;
            var result = await Task.Run(() => _roster.SetCharacterEnabled(root, defPath, enable));
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

    /// <summary>Saves which DEF this character folder uses (app data only; never renames the folder).</summary>
    public async Task SetPrimaryDefAsync(CharacterRowViewModel? row, string? relativeDef)
    {
        if (row is null || _root is null || _primaryDefs is null || string.IsNullOrWhiteSpace(relativeDef)) return;
        if (!row.Entry.DefCandidates.Any(c => string.Equals(c, relativeDef, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(System.IO.Path.GetFileName(c), relativeDef, StringComparison.OrdinalIgnoreCase)))
        {
            UserDialogs.Warn("That DEF is not part of this character folder.", "Primary DEF");
            return;
        }

        var folder = row.Entry.Id;
        var relative = relativeDef.Contains('/') || relativeDef.Contains('\\')
            ? relativeDef.Replace('\\', '/')
            : relativeDef;
        // Candidates are stored root-relative (chars/Folder/File.def); the store wants folder-relative.
        if (relative.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = "chars/" + folder + "/";
            if (relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                relative = relative[prefix.Length..];
        }

        try
        {
            await Task.Run(() => _primaryDefs.Set(_root, folder, relative));
            await _refreshLibrary();
        }
        catch (Exception ex)
        {
            UserDialogs.Warn(ex.Message, "Primary DEF");
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var keep = Selected;
        var filtered = _all.Where(row => query.Length == 0 || row.Matches(query));
        var ordered = DateAddedSort.Apply(
            filtered,
            SortMode,
            row => row.Entry.DateAddedUtc,
            row => row.DisplayName,
            row => row.Entry.DefPath);

        Characters.Clear();
        foreach (var row in ordered) Characters.Add(row);

        if (keep is not null && !Characters.Contains(keep)) Selected = Characters.FirstOrDefault();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasNoResults));
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<CharacterRowViewModel> rows, CancellationToken token)
    {
        var tasks = rows.Select(async row =>
        {
            var image = await _artwork.CharacterThumbnailAsync(row.Entry);
            if (!token.IsCancellationRequested) row.Thumbnail = image;
        });
        await Task.WhenAll(tasks);
    }

    private static async Task ScanFeaturesAsync(string root, IReadOnlyList<CharacterRowViewModel> rows, CancellationToken token)
    {
        foreach (var row in rows)
        {
            if (token.IsCancellationRequested) return;
            var features = await Task.Run(() =>
            {
                try { return CharacterFeatureScanner.Scan(root, row.Entry); }
                catch (Exception) { return null; }
            }, token).ConfigureAwait(true);
            if (features is not null && !token.IsCancellationRequested)
            {
                row.FeaturesText = string.Join(" · ", features.Labels());
            }
        }
    }

    private async Task LoadInspectorAsync(CharacterRowViewModel? row)
    {
        if (row is null || _root is null)
        {
            Inspector = null;
            return;
        }

        var inspector = new CharacterInspectorViewModel(row, SetPrimaryDefAsync, DeleteCharacterCommand) { Portrait = row.Thumbnail };
        Inspector = inspector;
        var root = _root;

        var detailsTask = Task.Run(() =>
        {
            try { return CharacterDetailsReader.Read(root, row.Entry); }
            catch (Exception) { return null; }
        });
        var portraitTask = _artwork.CharacterPortraitAsync(row.Entry);

        var details = await detailsTask;
        if (!ReferenceEquals(Inspector, inspector)) return;
        if (details is not null) inspector.Apply(details);

        var portrait = await portraitTask;
        if (ReferenceEquals(Inspector, inspector) && portrait is not null) inspector.Portrait = portrait;
    }

    private void OpenFolder(CharacterRowViewModel? row)
    {
        if (row is null || _root is null) return;
        var def = Path.GetFullPath(Path.Combine(_root, row.Entry.DefPath));
        if (!File.Exists(def)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{def}\"") { UseShellExecute = true });
    }

    private void CopyPath(CharacterRowViewModel? row)
    {
        if (row is null || _root is null) return;
        try
        {
            Clipboard.SetText(Path.GetFullPath(Path.Combine(_root, row.Entry.DefPath)));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }
}
