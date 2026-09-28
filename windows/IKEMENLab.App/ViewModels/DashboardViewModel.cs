using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Services;
using IKEMENLab.Core.Validation;

namespace IKEMENLab.App.ViewModels;

public enum HealthState
{
    Idle,
    Scanning,
    Clean,
    Warnings,
    Errors
}

public sealed class DashboardViewModel : ObservableObject
{
    private readonly IGameLauncher _launcher;
    private readonly Action<NavPage> _navigate;
    private readonly Services.ArtworkLoader _artwork;
    private LibrarySnapshot? _snapshot;
    private CancellationTokenSource? _backgroundWork;

    private string _rootPath = string.Empty;
    private int _characterCount;
    private int _stageCount;
    private int _nestedCount;
    private string _storageText = "—";
    private string _storageToolTip = "chars/ + stages/ on disk";
    private string _statusMessage = string.Empty;
    private bool _canLaunch;
    private bool _isGameRunning;
    private string _launchSubtitle = "Select an IKEMEN GO folder in Settings";

    private bool _quickSettingsAvailable;
    private bool _vSync;
    private bool _fullscreen;
    private double _masterVolume;
    private string _quickSettingsNote = "No save/config.ini found";

    private HealthState _healthState = HealthState.Idle;
    private string _healthStatusText = "Click 'Scan' to check for issues";
    private int _healthIssueCount;
    private bool _healthExpanded;

    private string? _dropNotice;

    public DashboardViewModel(IGameLauncher launcher, Services.ArtworkLoader artwork, Action<NavPage> navigate)
    {
        _launcher = launcher;
        _artwork = artwork;
        _navigate = navigate;
        LaunchCommand = new RelayCommand(Launch, () => CanLaunch && !IsGameRunning);
        OpenCharactersCommand = new RelayCommand(() => _navigate(NavPage.Characters));
        OpenStagesCommand = new RelayCommand(() => _navigate(NavPage.Stages));
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => _snapshot is { Installation.CanBrowse: true });
        ToggleHealthDetailsCommand = new RelayCommand(
            () => HealthExpanded = !HealthExpanded,
            () => HealthGroups.Count > 0);
        OpenRecentCommand = new RelayCommand(p =>
        {
            if (p is RecentItemViewModel item)
            {
                _navigate(item.IsCharacter ? NavPage.Characters : NavPage.Stages);
            }
        });
        DismissDropNoticeCommand = new RelayCommand(() => DropNotice = null);
    }

    public ICommand LaunchCommand { get; }
    public ICommand OpenCharactersCommand { get; }
    public ICommand OpenStagesCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ToggleHealthDetailsCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public ICommand DismissDropNoticeCommand { get; }

    public ObservableCollection<RecentItemViewModel> RecentItems { get; } = [];
    public ObservableCollection<HealthGroupViewModel> HealthGroups { get; } = [];

    public string RootPath { get => _rootPath; private set => SetProperty(ref _rootPath, value); }
    public int CharacterCount { get => _characterCount; private set => SetProperty(ref _characterCount, value); }
    public int StageCount { get => _stageCount; private set => SetProperty(ref _stageCount, value); }
    public int NestedCount { get => _nestedCount; private set => SetProperty(ref _nestedCount, value); }
    public string StorageText { get => _storageText; private set => SetProperty(ref _storageText, value); }
    public string StorageToolTip { get => _storageToolTip; private set => SetProperty(ref _storageToolTip, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string LaunchSubtitle { get => _launchSubtitle; private set => SetProperty(ref _launchSubtitle, value); }

    public bool CanLaunch
    {
        get => _canLaunch;
        private set
        {
            if (SetProperty(ref _canLaunch, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (SetProperty(ref _isGameRunning, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasRecentItems => RecentItems.Count > 0;

    // ---- Quick settings (read-only mirror of save/config.ini) ----
    public bool QuickSettingsAvailable { get => _quickSettingsAvailable; private set => SetProperty(ref _quickSettingsAvailable, value); }
    public bool VSync { get => _vSync; private set => SetProperty(ref _vSync, value); }
    public bool Fullscreen { get => _fullscreen; private set => SetProperty(ref _fullscreen, value); }

    public double MasterVolume
    {
        get => _masterVolume;
        private set
        {
            if (SetProperty(ref _masterVolume, value)) OnPropertyChanged(nameof(MasterVolumeText));
        }
    }

    public string MasterVolumeText => QuickSettingsAvailable ? $"{Math.Round(MasterVolume):0}%" : "—";
    public string QuickSettingsNote { get => _quickSettingsNote; private set => SetProperty(ref _quickSettingsNote, value); }

    // ---- Content health ----
    public HealthState HealthState
    {
        get => _healthState;
        private set
        {
            if (SetProperty(ref _healthState, value)) OnPropertyChanged(nameof(IsScanning));
        }
    }

    public bool IsScanning => HealthState == HealthState.Scanning;
    public string HealthStatusText { get => _healthStatusText; private set => SetProperty(ref _healthStatusText, value); }
    public int HealthIssueCount { get => _healthIssueCount; private set => SetProperty(ref _healthIssueCount, value); }
    public bool HealthExpanded { get => _healthExpanded; set => SetProperty(ref _healthExpanded, value); }

    // ---- Drop zone ----
    public string? DropNotice { get => _dropNotice; private set => SetProperty(ref _dropNotice, value); }

    public void ApplySnapshot(LibrarySnapshot? snapshot)
    {
        _backgroundWork?.Cancel();
        _backgroundWork = new CancellationTokenSource();
        _snapshot = snapshot;

        RecentItems.Clear();
        HealthGroups.Clear();
        HealthExpanded = false;
        HealthIssueCount = 0;
        HealthState = HealthState.Idle;
        HealthStatusText = "Click 'Scan' to check for issues";

        if (snapshot is null)
        {
            RootPath = string.Empty;
            CharacterCount = StageCount = NestedCount = 0;
            StorageText = "—";
            CanLaunch = false;
            LaunchSubtitle = "Select an IKEMEN GO folder in Settings";
            StatusMessage = "Choose an IKEMEN GO root in Settings.";
            ApplyQuickSettings(null);
            OnPropertyChanged(nameof(HasRecentItems));
            return;
        }

        RootPath = snapshot.Installation.RootPath;
        CharacterCount = snapshot.CharacterCount;
        StageCount = snapshot.StageCount;
        NestedCount = snapshot.NestedCharacterCount;
        CanLaunch = snapshot.Installation.CanLaunch;
        LaunchSubtitle = IsGameRunning ? "Running" : CanLaunch ? "Ready to play" : "Ikemen_GO.exe not found";

        StatusMessage = !snapshot.Installation.CanBrowse
            ? "Missing: " + string.Join(", ", snapshot.Installation.MissingItems)
            : $"Indexed {CharacterCount} characters ({NestedCount} nested), {StageCount} stages.";

        var now = DateTime.Now;
        foreach (var item in RecentContent.FromSnapshot(snapshot))
        {
            var row = new RecentItemViewModel(item, now);
            RecentItems.Add(row);
            _ = LoadThumbnailAsync(row, snapshot);
        }

        OnPropertyChanged(nameof(HasRecentItems));
        ApplyQuickSettings(snapshot);

        if (snapshot.Installation.CanBrowse)
        {
            _ = CalculateStorageAsync(snapshot.Installation.RootPath, _backgroundWork.Token);
            _ = ScanAsync();
        }
        else
        {
            StorageText = "—";
        }
    }

    private async Task LoadThumbnailAsync(RecentItemViewModel row, LibrarySnapshot snapshot)
    {
        Task<System.Windows.Media.ImageSource?>? load = row.IsCharacter
            ? snapshot.Characters.FirstOrDefault(c => c.DefPath == row.Item.DefPath) is { } c ? _artwork.CharacterThumbnailAsync(c) : null
            : snapshot.Stages.FirstOrDefault(s => s.RootRelativeDefPath == row.Item.DefPath) is { } s ? _artwork.StageThumbnailAsync(s) : null;
        if (load is null) return;
        var image = await load;
        if (ReferenceEquals(snapshot, _snapshot)) row.Thumbnail = image;
    }

    /// <summary>Called by the drop zone. Installing is a later safe-write phase; nothing is touched.</summary>
    public void HandleDroppedPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var names = string.Join(", ", paths.Take(3).Select(Path.GetFileName));
        if (paths.Count > 3) names += $" +{paths.Count - 3} more";
        DropNotice = $"Received {names}. Installing content is not enabled in this build yet, so nothing was copied or changed.";
    }

    private void ApplyQuickSettings(LibrarySnapshot? snapshot)
    {
        var config = snapshot?.Config;
        QuickSettingsAvailable = config is { Exists: true };
        VSync = config?.VSync ?? false;
        Fullscreen = config?.Fullscreen ?? false;
        MasterVolume = config?.MasterVolume ?? 0;
        OnPropertyChanged(nameof(MasterVolumeText));
        QuickSettingsNote = config is { Exists: true, SourcePath: { } path } && snapshot is not null
            ? $"Read from {Path.GetRelativePath(snapshot.Installation.RootPath, path).Replace('\\', '/')} · read-only"
            : "No save/config.ini found";
    }

    private async Task CalculateStorageAsync(string root, CancellationToken token)
    {
        StorageText = "…";
        StorageToolTip = "Calculating chars/ + stages/ size…";
        try
        {
            var bytes = await Task.Run(() => StorageCalculator.Calculate(root, token), token);
            if (token.IsCancellationRequested) return;
            StorageText = bytes is null ? "—" : StorageCalculator.Format(bytes.Value);
            StorageToolTip = bytes is null ? "chars/ or stages/ missing" : $"chars/ + stages/: {bytes.Value:N0} bytes";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer snapshot.
        }
        catch (Exception ex)
        {
            StorageText = "—";
            StorageToolTip = "Storage calculation failed: " + ex.Message;
        }
    }

    private async Task ScanAsync()
    {
        var snapshot = _snapshot;
        if (snapshot is null || !snapshot.Installation.CanBrowse) return;

        var token = _backgroundWork?.Token ?? CancellationToken.None;
        HealthState = HealthState.Scanning;
        HealthStatusText = "Scanning content…";

        IReadOnlyList<ValidationResult> results;
        try
        {
            results = await Task.Run(() => ContentValidator.ValidateLibrary(snapshot, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            HealthState = HealthState.Idle;
            HealthStatusText = "Scan failed: " + ex.Message;
            return;
        }

        if (!ReferenceEquals(snapshot, _snapshot)) return;

        HealthGroups.Clear();
        foreach (var result in results)
        {
            HealthGroups.Add(new HealthGroupViewModel(result));
        }

        var errors = results.Sum(r => r.ErrorCount);
        var warnings = results.Sum(r => r.WarningCount);
        HealthIssueCount = errors + warnings;

        if (errors == 0 && warnings == 0)
        {
            HealthState = HealthState.Clean;
            HealthStatusText = "✓ All content validated successfully";
            HealthExpanded = false;
        }
        else
        {
            HealthState = errors > 0 ? HealthState.Errors : HealthState.Warnings;
            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors} error(s)");
            if (warnings > 0) parts.Add($"{warnings} warning(s)");
            HealthStatusText = string.Join(", ", parts) + " — click to expand";
            HealthExpanded = true;
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void Launch()
    {
        if (!CanLaunch || IsGameRunning) return;
        var result = _launcher.Launch(RootPath);
        if (!result.Success)
        {
            LaunchSubtitle = "Launch failed";
            StatusMessage = $"Launch failed: {result.Error}";
            return;
        }

        StatusMessage = $"Launched Ikemen_GO.exe (PID {result.ProcessId}).";
        WatchProcess(result.ProcessId);
    }

    private void WatchProcess(int? processId)
    {
        if (processId is null) return;
        try
        {
            var process = Process.GetProcessById(processId.Value);
            process.EnableRaisingEvents = true;
            IsGameRunning = true;
            LaunchSubtitle = "Running";
            process.Exited += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsGameRunning = false;
                LaunchSubtitle = CanLaunch ? "Ready to play" : "Ikemen_GO.exe not found";
                process.Dispose();
            });
        }
        catch (ArgumentException)
        {
            // Process already exited.
            IsGameRunning = false;
        }
        catch (InvalidOperationException)
        {
            IsGameRunning = false;
        }
    }
}

public sealed class RecentItemViewModel : ObservableObject
{
    private System.Windows.Media.ImageSource? _thumbnail;

    public RecentItemViewModel(RecentContentItem item, DateTime nowLocal)
    {
        Item = item;
        DateText = RecentContent.FormatInstallDate(item.InstalledAtUtc, nowLocal);
        Initial = string.IsNullOrEmpty(item.Name) ? "?" : item.Name[..1].ToUpperInvariant();
    }

    public RecentContentItem Item { get; }

    public System.Windows.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
    public string Name => Item.Name;
    public string Author => Item.Author;
    public bool IsCharacter => Item.Type == RecentContentType.Character;
    public string TypeLabel => IsCharacter ? "Char" : "Stage";
    public string DateText { get; }
    public string Initial { get; }
    public bool IsEnabledInRoster => Item.Status == ContentStatus.Active;

    public string StatusToolTip => Item.Status switch
    {
        ContentStatus.Active => "Enabled in select.def (read-only view)",
        ContentStatus.Disabled => "Commented out in select.def (read-only view)",
        _ => "Not listed in select.def (read-only view)"
    };

    public string ToolTip => $"{Item.DefPath}\nInstalled {Item.InstalledAtUtc.ToLocalTime():g}";
}

public sealed class HealthGroupViewModel
{
    public HealthGroupViewModel(ValidationResult result)
    {
        Name = result.ContentName;
        TypeLabel = result.ContentType.ToUpperInvariant();
        Path = result.ContentPath;
        TypeGlyph = result.ContentType switch
        {
            "character" => "",
            "stage" => "",
            _ => ""
        };
        Issues = result.Issues
            .Where(i => i.Severity != ValidationSeverity.Info)
            .Select(i => new HealthIssueViewModel(i))
            .ToList();
    }

    public string Name { get; }
    public string TypeLabel { get; }
    public string TypeGlyph { get; }
    public string Path { get; }
    public IReadOnlyList<HealthIssueViewModel> Issues { get; }
}

public sealed class HealthIssueViewModel(ValidationIssue issue)
{
    public string Message => issue.Message;
    public string? Suggestion => issue.Suggestion is null ? null : "→ " + issue.Suggestion;
    public bool IsError => issue.Severity == ValidationSeverity.Error;
    public string Glyph => IsError ? "\uEB90" : "\uE814";
}
