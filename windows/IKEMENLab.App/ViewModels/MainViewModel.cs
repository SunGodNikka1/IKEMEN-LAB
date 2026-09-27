using System.Collections.ObjectModel;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Services;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly LibraryIndexService _indexService;
    private object? _currentPage;
    private NavPage _selectedNav = NavPage.Dashboard;
    private string _statusText = "Ready";
    private bool _isBusy;
    private LibrarySnapshot? _snapshot;

    public MainViewModel(
        ISettingsStore settingsStore,
        LibraryIndexService indexService,
        GameLauncher launcher,
        Services.FolderPicker folderPicker)
    {
        _settingsStore = settingsStore;
        _indexService = indexService;

        Dashboard = new DashboardViewModel(launcher);
        Characters = new CharactersViewModel();
        Settings = new SettingsViewModel(settingsStore, folderPicker, root => _ = RefreshAsync(root));
        StagesPlaceholder = new PlaceholderViewModel
        {
            Title = "Stages",
            Message = "Stage browser is a placeholder in WIN-1. Stage count is available on the Dashboard."
        };
        ScreenpacksPlaceholder = new PlaceholderViewModel
        {
            Title = "Screenpacks",
            Message = "Screenpack management is deferred past WIN-1."
        };
        CollectionsPlaceholder = new PlaceholderViewModel
        {
            Title = "Collections",
            Message = "Collections are deferred past WIN-1. WIN-1 never writes select.def."
        };

        NavItems =
        [
            new NavItem { Page = NavPage.Dashboard, Title = "Dashboard", IconGlyph = "⌂" },
            new NavItem { Page = NavPage.Characters, Title = "Characters", IconGlyph = "♟" },
            new NavItem { Page = NavPage.Stages, Title = "Stages", IconGlyph = "▤" },
            new NavItem { Page = NavPage.Screenpacks, Title = "Screenpacks", IconGlyph = "▦" },
            new NavItem { Page = NavPage.Collections, Title = "Collections", IconGlyph = "☰" },
            new NavItem { Page = NavPage.Settings, Title = "Settings", IconGlyph = "⚙" }
        ];

        NavigateCommand = new RelayCommand(p =>
        {
            if (p is NavPage page) Navigate(page);
            else if (p is string s && Enum.TryParse<NavPage>(s, out var parsed)) Navigate(parsed);
        });

        RefreshCommand = new AsyncRelayCommand(async () => await RefreshAsync(null));
        Navigate(NavPage.Dashboard);
    }

    public ObservableCollection<NavItem> NavItems { get; }
    public DashboardViewModel Dashboard { get; }
    public CharactersViewModel Characters { get; }
    public SettingsViewModel Settings { get; }
    public PlaceholderViewModel StagesPlaceholder { get; }
    public PlaceholderViewModel ScreenpacksPlaceholder { get; }
    public PlaceholderViewModel CollectionsPlaceholder { get; }

    public ICommand NavigateCommand { get; }
    public ICommand RefreshCommand { get; }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public NavPage SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (SetProperty(ref _selectedNav, value))
            {
                Navigate(value);
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public LibrarySnapshot? Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public async Task InitializeAsync()
    {
        var settings = _settingsStore.Load();
        Settings.LoadFrom(settings, null);
        await RefreshAsync(settings.IkemenRoot);
    }

    public async Task RefreshAsync(string? rootOverride)
    {
        IsBusy = true;
        StatusText = "Indexing…";
        try
        {
            var settings = _settingsStore.Load();
            var root = rootOverride ?? settings.IkemenRoot;
            if (!string.IsNullOrWhiteSpace(rootOverride) &&
                !string.Equals(settings.IkemenRoot, rootOverride, StringComparison.OrdinalIgnoreCase))
            {
                settings.IkemenRoot = rootOverride;
                _settingsStore.Save(settings);
            }

            LibrarySnapshot? snapshot = null;
            if (!string.IsNullOrWhiteSpace(root))
            {
                snapshot = await Task.Run(() => _indexService.Index(root));
            }

            Snapshot = snapshot;
            Dashboard.ApplySnapshot(snapshot);
            Characters.ApplySnapshot(snapshot);
            Settings.LoadFrom(_settingsStore.Load(), snapshot?.Installation);

            UpdateBadges(snapshot);

            if (snapshot is null)
            {
                StatusText = "Select an IKEMEN GO folder in Settings.";
            }
            else if (!snapshot.Installation.CanBrowse)
            {
                StatusText = "Installation incomplete.";
            }
            else
            {
                StatusText =
                    $"{snapshot.CharacterCount} characters · {snapshot.StageCount} stages · read-only index";
                StagesPlaceholder.Message =
                    $"WIN-1 placeholder. Indexed stage count: {snapshot.StageCount}. Full stage browser comes later.";
            }
        }
        catch (Exception ex)
        {
            StatusText = "Index failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateBadges(LibrarySnapshot? snapshot)
    {
        foreach (var item in NavItems)
        {
            item.Badge = item.Page switch
            {
                NavPage.Characters => snapshot?.CharacterCount.ToString() ?? "0",
                NavPage.Stages => snapshot?.StageCount.ToString() ?? "0",
                _ => null
            };
        }

        OnPropertyChanged(nameof(NavItems));
    }

    private void Navigate(NavPage page)
    {
        _selectedNav = page;
        OnPropertyChanged(nameof(SelectedNav));
        CurrentPage = page switch
        {
            NavPage.Dashboard => Dashboard,
            NavPage.Characters => Characters,
            NavPage.Stages => StagesPlaceholder,
            NavPage.Screenpacks => ScreenpacksPlaceholder,
            NavPage.Collections => CollectionsPlaceholder,
            NavPage.Settings => Settings,
            _ => Dashboard
        };
    }
}
