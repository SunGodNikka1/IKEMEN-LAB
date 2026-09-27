using System.Windows;
using IKEMENLab.App.Services;
using IKEMENLab.App.ViewModels;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Services;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DefFileReader.EnsureEncodingsRegistered();
        AppDataPaths.EnsureAppDataDirectory();

        var settingsStore = new JsonSettingsStore();
        var indexService = new LibraryIndexService();
        var launcher = new GameLauncher();
        var folderPicker = new FolderPicker();
        var mainVm = new MainViewModel(settingsStore, indexService, launcher, folderPicker);

        var window = new MainWindow(mainVm);
        MainWindow = window;
        window.Show();
    }
}
