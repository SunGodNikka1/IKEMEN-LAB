using System.Collections.ObjectModel;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
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

        Artwork = new ArtworkLoader();
        Dashboard = new DashboardViewModel(
            launcher,
            Artwork,
            page => Navigate(page),
            async () => await RefreshAsync(null));
        Characters = new CharactersViewModel(Artwork, page => Navigate(page));
        Settings = new SettingsViewModel(settingsStore, folderPicker, root => _ = RefreshAsync(root));
        Stages = new StagesViewModel(Artwork, page => Navigate(page));
        Screenpacks = new ScreenpacksViewModel(Artwork, page => Navigate(page));
        Collections = new CollectionsViewModel(Artwork);

        NavItems =
        [
            new NavItem { Page = NavPage.Dashboard, Title = "Dashboard", IconGlyph = "\uF0E2" },
            new NavItem { Page = NavPage.Characters, Title = "Characters", IconGlyph = "\uE77B", Badge = "0" },
            new NavItem { Page = NavPage.Stages, Title = "Stages", IconGlyph = "\uE91B", Badge = "0" },
            new NavItem { Page = NavPage.Screenpacks, Title = "Screenpacks", IconGlyph = "\uE81E" },
            new NavItem { Page = NavPage.Collections, Title = "Collections", IconGlyph = "\uE8B7" }
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
    public ArtworkLoader Artwork { get; }
    public NavItem SettingsNav { get; }
    public DashboardViewModel Dashboard { get; }
    public CharactersViewModel Characters { get; }
    public SettingsViewModel Settings { get; }
    public StagesViewModel Stages { get; }
    public ScreenpacksViewModel Screenpacks { get; }
    public CollectionsViewModel Collections { get; }

    // SYSTEM > GPU readout. Neutral until a real reading exists (never a fabricated value).
    private string _gpuPercentText = "—";
    private double _gpuFraction;
    private string _gpuToolTip = "GPU memory readout unavailable";
    private GpuMemoryMonitor? _gpuMonitor;
    private System.Windows.Threading.DispatcherTimer? _gpuTimer;
    private bool _gpuSampling;

    public string GpuPercentText { get => _gpuPercentText; private set => SetProperty(ref _gpuPercentText, value); }
    public double GpuFraction { get => _gpuFraction; private set => SetProperty(ref _gpuFraction, value); }
    public string GpuToolTip { get => _gpuToolTip; private set => SetProperty(ref _gpuToolTip, value); }

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
        StartGpuMonitor();
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
            Artwork.SetRoot(snapshot is { Installation.CanBrowse: true } ? snapshot.Installation.RootPath : null);
            Dashboard.ApplySnapshot(snapshot);
            Characters.ApplySnapshot(snapshot);
            Stages.ApplySnapshot(snapshot);
            Screenpacks.ApplySnapshot(snapshot);
            Collections.ApplySnapshot(snapshot);
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

    private void StartGpuMonitor()
    {
        if (_gpuTimer is not null) return;
        _gpuTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gpuTimer.Tick += async (_, _) => await SampleGpuAsync();
        _gpuTimer.Start();
        _ = SampleGpuAsync();
    }

    private async Task SampleGpuAsync()
    {
        if (_gpuSampling) return;
        _gpuSampling = true;
        try
        {
            var reading = await Task.Run(() =>
            {
                _gpuMonitor ??= new GpuMemoryMonitor();
                return _gpuMonitor.Read();
            });

            if (reading is null)
            {
                GpuPercentText = "—";
                GpuFraction = 0;
                GpuToolTip = "GPU memory readout unavailable";
                if (_gpuMonitor is { IsAvailable: false }) _gpuTimer?.Stop();
                return;
            }

            GpuFraction = reading.Fraction;
            GpuPercentText = $"{Math.Round(reading.Fraction * 100):0}%";
            GpuToolTip = $"{reading.AdapterName}\nDedicated memory: {reading.DedicatedUsedBytes / 1048576.0:N0} / {reading.DedicatedTotalBytes / 1048576.0:N0} MB (system-wide)";
        }
        catch (Exception)
        {
            GpuPercentText = "—";
            GpuFraction = 0;
        }
        finally
        {
            _gpuSampling = false;
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
            NavPage.Stages => Stages,
            NavPage.Screenpacks => Screenpacks,
            NavPage.Collections => Collections,
            NavPage.Settings => Settings,
            _ => Dashboard
        };
    }
}
