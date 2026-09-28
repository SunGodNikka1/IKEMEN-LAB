using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Config;
using IKEMENLab.Core.Install;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Services;
using IKEMENLab.Core.Validation;
using Microsoft.Win32;

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
    private readonly Func<Task> _refreshLibrary;
    private readonly IContentInstallService _installer;
    private readonly FullgameImporter _fullgame;
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
    private bool _canEditVSync;
    private bool _canEditFullscreen;
    private bool _canEditMasterVolume;
    private bool _vSync;
    private bool _fullscreen;
    private double _masterVolume;
    private string _quickSettingsNote = "No save/config.ini found";
    private bool _suppressQuickSettingsWrite;
    private bool _quickSettingsBusy;
    private int? _committedMasterVolume;
    private readonly IIkemenConfigMutationService _configWriter;

    private HealthState _healthState = HealthState.Idle;
    private string _healthStatusText = "Click 'Scan' to check for issues";
    private int _healthIssueCount;
    private bool _healthExpanded;

    private string? _dropNotice;
    private bool _installBusy;

    public DashboardViewModel(
        IGameLauncher launcher,
        Services.ArtworkLoader artwork,
        Action<NavPage> navigate,
        Func<Task> refreshLibrary,
        IContentInstallService? installer = null,
        IIkemenConfigMutationService? configWriter = null,
        FullgameImporter? fullgame = null)
    {
        _launcher = launcher;
        _artwork = artwork;
        _navigate = navigate;
        _refreshLibrary = refreshLibrary;
        _installer = installer ?? new ContentInstallService();
        _fullgame = fullgame ?? new FullgameImporter();
        _configWriter = configWriter ?? new IkemenConfigMutationService();
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
        BrowseInstallFilesCommand = new AsyncRelayCommand(BrowseFilesAsync, () => !_installBusy && CanInstall);
        BrowseInstallFolderCommand = new AsyncRelayCommand(BrowseFolderAsync, () => !_installBusy && CanInstall);
        CommitMasterVolumeCommand = new AsyncRelayCommand(CommitMasterVolumeAsync, () => CanEditMasterVolume && !_quickSettingsBusy);
    }

    public ICommand LaunchCommand { get; }
    public ICommand OpenCharactersCommand { get; }
    public ICommand OpenStagesCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ToggleHealthDetailsCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public ICommand DismissDropNoticeCommand { get; }
    public ICommand BrowseInstallFilesCommand { get; }
    public ICommand BrowseInstallFolderCommand { get; }
    public ICommand CommitMasterVolumeCommand { get; }

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

    public bool CanInstall => !string.IsNullOrWhiteSpace(RootPath) && _snapshot is { Installation.CanBrowse: true };

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

    public bool QuickSettingsAvailable { get => _quickSettingsAvailable; private set => SetProperty(ref _quickSettingsAvailable, value); }
    public bool CanEditVSync { get => _canEditVSync; private set => SetProperty(ref _canEditVSync, value); }
    public bool CanEditFullscreen { get => _canEditFullscreen; private set => SetProperty(ref _canEditFullscreen, value); }
    public bool CanEditMasterVolume { get => _canEditMasterVolume; private set => SetProperty(ref _canEditMasterVolume, value); }

    public bool VSync
    {
        get => _vSync;
        set
        {
            if (!SetProperty(ref _vSync, value)) return;
            if (!_suppressQuickSettingsWrite && CanEditVSync)
                _ = WriteBoolAsync(ConfigValueKind.VSync, value, () => VSync = !value);
        }
    }

    public bool Fullscreen
    {
        get => _fullscreen;
        set
        {
            if (!SetProperty(ref _fullscreen, value)) return;
            if (!_suppressQuickSettingsWrite && CanEditFullscreen)
                _ = WriteBoolAsync(ConfigValueKind.Fullscreen, value, () => Fullscreen = !value);
        }
    }

    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            if (SetProperty(ref _masterVolume, value)) OnPropertyChanged(nameof(MasterVolumeText));
        }
    }

    public string MasterVolumeText => QuickSettingsAvailable ? $"{Math.Round(MasterVolume):0}%" : "—";
    public string QuickSettingsNote { get => _quickSettingsNote; private set => SetProperty(ref _quickSettingsNote, value); }

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
            OnPropertyChanged(nameof(CanInstall));
            CommandManager.InvalidateRequerySuggested();
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
        OnPropertyChanged(nameof(CanInstall));
        CommandManager.InvalidateRequerySuggested();
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

    public void HandleDroppedPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _ = BeginInstallFlowAsync(paths);
    }

    private async Task BrowseFilesAsync()
    {
        if (!CanInstall)
        {
            DropNotice = "Select a valid IKEMEN GO folder in Settings first.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select content archives (character, stage, screenpack, or fullgame)",
            Multiselect = true,
            Filter = "Archives (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;
        await BeginInstallFlowAsync(dialog.FileNames);
    }

    private async Task BrowseFolderAsync()
    {
        if (!CanInstall)
        {
            DropNotice = "Select a valid IKEMEN GO folder in Settings first.";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select content folder (character, stage, screenpack, or fullgame)",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true || dialog.FolderNames.Length == 0) return;
        await BeginInstallFlowAsync(dialog.FolderNames);
    }

    private async Task BeginInstallFlowAsync(IReadOnlyList<string> paths)
    {
        if (_installBusy) return;
        if (!CanInstall || string.IsNullOrWhiteSpace(RootPath))
        {
            DropNotice = "Select a valid IKEMEN GO folder in Settings before installing.";
            return;
        }

        _installBusy = true;
        DropNotice = "Inspecting content…";
        CommandManager.InvalidateRequerySuggested();

        // Prefer fullgame when a package looks like a multi-content dump.
        try
        {
            var handled = await TryFullgameInstallAsync(paths);
            if (handled)
            {
                _installBusy = false;
                CommandManager.InvalidateRequerySuggested();
                return;
            }
        }
        catch (Exception ex)
        {
            DropNotice = "Fullgame inspect failed: " + ex.Message;
            _installBusy = false;
            return;
        }

        InspectBatchResult inspect;
        try
        {
            var root = RootPath;
            inspect = await Task.Run(() => _installer.Inspect(paths, root));
        }
        catch (Exception ex)
        {
            DropNotice = "Inspect failed: " + ex.Message;
            _installBusy = false;
            return;
        }

        if (inspect.Items.Count == 0)
        {
            DropNotice = inspect.Failures.Count > 0
                ? string.Join(" · ", inspect.Failures.Select(f => f.Reason).Take(3))
                : "No character, stage, or screenpack packages detected.";
            _installer.CleanupStaging(inspect.StagingDirectories);
            _installBusy = false;
            return;
        }

        DropNotice = null;
        var previewVm = new InstallPreviewViewModel(inspect);
        var window = new InstallPreviewWindow(previewVm)
        {
            Owner = Application.Current?.MainWindow
        };

        var confirmed = window.ShowDialog() == true;
        if (!confirmed)
        {
            _installer.CleanupStaging(inspect.StagingDirectories);
            DropNotice = "Install cancelled.";
            _installBusy = false;
            return;
        }

        previewVm.ApplyDecisionsToItems();
        DropNotice = "Installing…";

        InstallBatchResult result;
        try
        {
            var root = RootPath;
            result = await Task.Run(() => _installer.Execute(inspect.Items, root, dryRun: false));
        }
        catch (Exception ex)
        {
            DropNotice = "Install failed: " + ex.Message;
            _installer.CleanupStaging(inspect.StagingDirectories);
            _installBusy = false;
            return;
        }

        _installer.CleanupStaging(inspect.StagingDirectories);

        var parts = new List<string>();
        if (result.InstalledCount > 0) parts.Add($"{result.InstalledCount} installed");
        if (result.SkippedCount > 0) parts.Add($"{result.SkippedCount} skipped");
        if (result.FailedCount > 0)
        {
            var firstFail = result.Items.FirstOrDefault(i =>
                i.Outcome is InstallItemOutcome.Failed or InstallItemOutcome.Rejected);
            parts.Add($"{result.FailedCount} failed" + (firstFail?.Error is { } err ? $": {err}" : string.Empty));
        }

        DropNotice = parts.Count == 0 ? "Nothing installed." : string.Join(" · ", parts);

        if (result.InstalledCount > 0)
        {
            try { await _refreshLibrary(); }
            catch (Exception ex) { DropNotice += " · Refresh failed: " + ex.Message; }
        }

        _installBusy = false;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Returns true when a fullgame package was previewed (and optionally imported).</summary>
    private async Task<bool> TryFullgameInstallAsync(IReadOnlyList<string> paths)
    {
        var staging = new List<string>();
        FullgameManifest? chosen = null;
        try
        {
            foreach (var input in paths)
            {
                if (string.IsNullOrWhiteSpace(input)) continue;
                var full = Path.GetFullPath(input);
                string packageDir;
                if (Directory.Exists(full))
                {
                    packageDir = full;
                }
                else if (File.Exists(full) && ArchiveExtractor.IsArchiveFile(full))
                {
                    packageDir = await Task.Run(() => ArchiveExtractor.ExtractToStaging(full));
                    staging.Add(packageDir);
                }
                else continue;

                var manifest = await Task.Run(() => _fullgame.Scan(packageDir, RootPath));
                if (manifest.IsFullgame)
                {
                    chosen = manifest;
                    break;
                }
            }

            if (chosen is null)
            {
                _installer.CleanupStaging(staging);
                return false;
            }

            var msg =
                $"Fullgame package detected: {chosen.SourceFolderName}\n\n" +
                $"Characters: {chosen.Characters.Count}\n" +
                $"Stages: {chosen.Stages.Count}\n" +
                $"Screenpack: {(chosen.Screenpack is null ? "No" : chosen.Screenpack.DisplayName)}\n" +
                $"Fonts: {chosen.Fonts.Count}\n" +
                $"Sounds: {chosen.Sounds.Count}\n\n" +
                $"Collection: {chosen.SuggestedCollectionName}\n\n" +
                "Import into the current IKEMEN install?\n" +
                "(select.def and Motif will not change. Engine binaries are never copied.)";

            DropNotice = null;
            var confirm = MessageBox.Show(
                Application.Current?.MainWindow,
                msg,
                "Fullgame import",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (confirm != MessageBoxResult.OK)
            {
                DropNotice = "Fullgame import cancelled.";
                _installer.CleanupStaging(staging);
                return true;
            }

            DropNotice = "Importing fullgame…";
            var root = RootPath;
            var manifestToInstall = chosen;
            var result = await Task.Run(() => _fullgame.Install(manifestToInstall, root, (name, type) =>
            {
                var answer = MessageBox.Show(
                    Application.Current?.MainWindow,
                    $"\"{name}\" ({type}) already exists.\n\nYes = Replace\nNo = Skip\nCancel = Skip all remaining",
                    "Duplicate content",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                return answer switch
                {
                    MessageBoxResult.Yes => FullgameDuplicateAction.Replace,
                    MessageBoxResult.Cancel => FullgameDuplicateAction.SkipAll,
                    _ => FullgameDuplicateAction.Skip
                };
            }));

            DropNotice = result.Summary +
                (result.CollectionCreated is { } c ? $" · Collection \"{c.Name}\" created." : "") +
                (result.TotalFailed > 0 ? " · Some items failed — see details in status." : "");
            if (result.TotalInstalled > 0)
            {
                try { await _refreshLibrary(); }
                catch (Exception ex) { DropNotice += " · Refresh failed: " + ex.Message; }
            }

            _installer.CleanupStaging(staging);
            return true;
        }
        catch
        {
            _installer.CleanupStaging(staging);
            throw;
        }
    }

    private void ApplyQuickSettings(LibrarySnapshot? snapshot)
    {
        var config = snapshot?.Config;
        _suppressQuickSettingsWrite = true;
        try
        {
            QuickSettingsAvailable = config is { Exists: true };
            CanEditVSync = config?.VSync is not null;
            CanEditFullscreen = config?.Fullscreen is not null;
            CanEditMasterVolume = config?.MasterVolume is not null;
            VSync = config?.VSync ?? false;
            Fullscreen = config?.Fullscreen ?? false;
            MasterVolume = config?.MasterVolume ?? 0;
            _committedMasterVolume = config?.MasterVolume;
            OnPropertyChanged(nameof(MasterVolumeText));
            QuickSettingsNote = config is { Exists: true, SourcePath: { } path } && snapshot is not null
                ? $"Editing {Path.GetRelativePath(snapshot.Installation.RootPath, path).Replace('\\', '/')}"
                : "No save/config.ini found";
        }
        finally
        {
            _suppressQuickSettingsWrite = false;
        }
    }

    private async Task WriteBoolAsync(ConfigValueKind kind, bool value, Action revert)
    {
        if (_snapshot?.Installation.RootPath is not { } root || _quickSettingsBusy) return;
        _quickSettingsBusy = true;
        try
        {
            var result = await Task.Run(() => _configWriter.SetBool(root, kind, value));
            if (!result.Success)
            {
                _suppressQuickSettingsWrite = true;
                revert();
                _suppressQuickSettingsWrite = false;
                QuickSettingsNote = result.Error ?? "Config write failed.";
                return;
            }

            var re = result.ResultingConfig ?? IkemenConfigReader.Read(root);
            _suppressQuickSettingsWrite = true;
            if (kind == ConfigValueKind.VSync) VSync = re.VSync ?? value;
            if (kind == ConfigValueKind.Fullscreen) Fullscreen = re.Fullscreen ?? value;
            _suppressQuickSettingsWrite = false;
            QuickSettingsNote = result.Changed ? $"Updated {kind}." : "Already set.";
        }
        catch (Exception ex)
        {
            _suppressQuickSettingsWrite = true;
            revert();
            _suppressQuickSettingsWrite = false;
            QuickSettingsNote = ex.Message;
        }
        finally
        {
            _quickSettingsBusy = false;
        }
    }

    private async Task CommitMasterVolumeAsync()
    {
        if (_snapshot?.Installation.RootPath is not { } root || !CanEditMasterVolume || _quickSettingsBusy) return;
        _quickSettingsBusy = true;
        // Revert target is the last value known to be on disk, not the (possibly stale) snapshot.
        var previous = _committedMasterVolume ?? _snapshot.Config?.MasterVolume ?? (int)Math.Round(MasterVolume);
        try
        {
            var volume = (int)Math.Clamp(Math.Round(MasterVolume), 0, 100);
            var result = await Task.Run(() => _configWriter.SetMasterVolume(root, volume));
            if (!result.Success)
            {
                _suppressQuickSettingsWrite = true;
                MasterVolume = previous;
                _suppressQuickSettingsWrite = false;
                QuickSettingsNote = result.Error ?? "Volume write failed.";
                return;
            }

            var re = result.ResultingConfig ?? IkemenConfigReader.Read(root);
            _suppressQuickSettingsWrite = true;
            MasterVolume = re.MasterVolume ?? volume;
            _committedMasterVolume = re.MasterVolume ?? volume;
            _suppressQuickSettingsWrite = false;
            QuickSettingsNote = result.Changed ? $"Master Volume set to {MasterVolume:0}%." : "Already set.";
        }
        catch (Exception ex)
        {
            _suppressQuickSettingsWrite = true;
            MasterVolume = previous;
            _suppressQuickSettingsWrite = false;
            QuickSettingsNote = ex.Message;
        }
        finally
        {
            _quickSettingsBusy = false;
        }
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
            HealthGroups.Add(new HealthGroupViewModel(result));

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
            "character" => "\uE77B",
            "stage" => "\uE91B",
            _ => "\uE8B7"
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
