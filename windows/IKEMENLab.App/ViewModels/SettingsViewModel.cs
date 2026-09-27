using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly FolderPicker _folderPicker;
    private readonly Action<string?> _onRootChanged;
    private string _ikemenRoot = string.Empty;
    private string _validationSummary = string.Empty;
    private string _settingsPath = string.Empty;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        FolderPicker folderPicker,
        Action<string?> onRootChanged)
    {
        _settingsStore = settingsStore;
        _folderPicker = folderPicker;
        _onRootChanged = onRootChanged;
        BrowseCommand = new RelayCommand(Browse);
        SettingsPath = AppDataPaths.GetSettingsPath();
    }

    public ICommand BrowseCommand { get; }

    public string IkemenRoot
    {
        get => _ikemenRoot;
        set => SetProperty(ref _ikemenRoot, value);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    public string SettingsPath
    {
        get => _settingsPath;
        private set => SetProperty(ref _settingsPath, value);
    }

    public void LoadFrom(AppSettings settings, InstallationCheck? check)
    {
        IkemenRoot = settings.IkemenRoot ?? string.Empty;
        ApplyCheck(check);
    }

    public void ApplyCheck(InstallationCheck? check)
    {
        if (check is null || string.IsNullOrWhiteSpace(check.RootPath))
        {
            ValidationSummary = "No installation selected.";
            return;
        }

        if (check.CanLaunch)
        {
            ValidationSummary = "Valid installation — browsing and Launch enabled.";
        }
        else if (check.CanBrowse)
        {
            ValidationSummary = "Structure OK — Ikemen_GO.exe missing (Launch disabled).";
        }
        else
        {
            ValidationSummary = "Missing: " + string.Join(", ", check.MissingItems);
        }
    }

    private void Browse()
    {
        var picked = _folderPicker.PickFolder(string.IsNullOrWhiteSpace(IkemenRoot) ? null : IkemenRoot);
        if (picked is null) return;

        IkemenRoot = picked;
        var settings = _settingsStore.Load();
        settings.IkemenRoot = picked;
        _settingsStore.Save(settings);
        _onRootChanged(picked);
    }
}
