using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Services;

namespace IKEMENLab.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly IGameLauncher _launcher;
    private string _rootPath = "No IKEMEN installation selected";
    private int _characterCount;
    private int _stageCount;
    private int _nestedCount;
    private string _statusMessage = string.Empty;
    private bool _canLaunch;

    public DashboardViewModel(IGameLauncher launcher)
    {
        _launcher = launcher;
        LaunchCommand = new RelayCommand(Launch, () => CanLaunch);
    }

    public ICommand LaunchCommand { get; }

    public string RootPath
    {
        get => _rootPath;
        private set => SetProperty(ref _rootPath, value);
    }

    public int CharacterCount
    {
        get => _characterCount;
        private set => SetProperty(ref _characterCount, value);
    }

    public int StageCount
    {
        get => _stageCount;
        private set => SetProperty(ref _stageCount, value);
    }

    public int NestedCount
    {
        get => _nestedCount;
        private set => SetProperty(ref _nestedCount, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool CanLaunch
    {
        get => _canLaunch;
        private set
        {
            if (SetProperty(ref _canLaunch, value))
            {
                (LaunchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public void ApplySnapshot(LibrarySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            RootPath = "No IKEMEN installation selected";
            CharacterCount = 0;
            StageCount = 0;
            NestedCount = 0;
            CanLaunch = false;
            StatusMessage = "Choose an IKEMEN GO root in Settings.";
            return;
        }

        RootPath = snapshot.Installation.RootPath;
        CharacterCount = snapshot.CharacterCount;
        StageCount = snapshot.StageCount;
        NestedCount = snapshot.NestedCharacterCount;
        CanLaunch = snapshot.Installation.CanLaunch;

        if (!snapshot.Installation.CanBrowse)
        {
            StatusMessage = "Missing: " + string.Join(", ", snapshot.Installation.MissingItems);
        }
        else if (!snapshot.Installation.CanLaunch)
        {
            StatusMessage = "Browsing OK — Ikemen_GO.exe missing, Launch disabled.";
        }
        else
        {
            StatusMessage = $"Indexed {CharacterCount} characters ({NestedCount} nested), {StageCount} stages.";
        }
    }

    private void Launch()
    {
        if (!CanLaunch) return;
        var result = _launcher.Launch(RootPath);
        StatusMessage = result.Success
            ? $"Launched Ikemen_GO.exe (PID {result.ProcessId})."
            : $"Launch failed: {result.Error}";
    }
}
