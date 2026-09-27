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
            new NavItem { Page = NavPage.Dashboard, Title = "Dashboard", IconGlyph = "\uF0E2" },
            new NavItem { Page = NavPage.Characters, Title = "Characters", IconGlyph = "\uE77B", Badge = "0" },
            new NavItem { Page = NavPage.Stages, Title = "Stages", IconGlyph = "\uE91B", Badge = "0" },
            new NavItem { Page = NavPage.Screenpacks, Title = "Screenpacks", IconGlyph = "\uE81E" }
        ];
        SettingsNav = new NavItem { Page = NavPage.Settings, Title = "Settings", IconGlyph = "\uE713" };

        NavigateCommand = new RelayCommand(p =>
        {
            if (p is NavPage page) Navigate(page);
            else if (p is string s && Enum.TryParse<NavPage>(s, out var parsed)) Navigate(parsed);
        });

        RefreshCommand = new AsyncRelayCommand(async () => await RefreshAsync(null));
        Navigate(NavPage.Dashboard);
    }

    public ObservableCollection<NavItem> NavItems { get; }
    public NavItem SettingsNav { get; }
    public DashboardViewModel Dashboard { get; }
    public CharactersViewModel Characters { get; }
    public SettingsViewModel Settings { get; }
    public PlaceholderViewModel StagesPlaceholder { get; }
    public PlaceholderViewModel ScreenpacksPlaceholder { get; }
    public PlaceholderViewModel CollectionsPlaceholder { get; }

    // SYSTEM › GPU readout. Neutral until a real monitor feeds it (never a fabricated value).
    public string GpuPercentText { get; private set; } = "—";
    public double GpuFillWidth { get; private set; }
    public string GpuToolTip { get; private set; } = "GPU memory readout unavailable";

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
                NavPage.Characters => (snapshot?.CharacterCount ?? 0).ToString(),
                NavPage.Stages => (snapshot?.StageCount ?? 0).ToString(),
                _ => null
            };
        }
    }

    private void Navigate(NavPage page)
    {
        _selectedNav = page;
        OnPropertyChanged(nameof(SelectedNav));
        foreach (var item in NavItems)
        {
            item.IsSelected = item.Page == page;
        }
        SettingsNav.IsSelected = page == NavPage.Settings;
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
