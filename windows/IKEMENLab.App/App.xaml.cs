using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        var options = QaOptions.Parse(e.Args);

        if (options.Page is { } page) mainVm.SelectedNav = page;
        if (options.View is { } view) mainVm.Characters.ViewMode = view;

        var window = new MainWindow(mainVm);
        MainWindow = window;
        if (options.Width > 0 && options.Height > 0)
        {
            window.Width = options.Width;
            window.Height = options.Height;
        }

        if (options.SnapshotPath is not null)
        {
            // Render our own visual tree; never activate or cover other windows (e.g. a running game).
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = SystemParameters.VirtualScreenLeft + 20;
            window.Top = SystemParameters.VirtualScreenTop + 20;
            window.Loaded += (_, _) => ScheduleSnapshot(window, options);
        }

        window.Show();
    }

    private void ScheduleSnapshot(Window window, QaOptions options)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(options.SnapshotDelaySeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (options.AfterLoad is { } action) action(window);
                window.UpdateLayout();
                SaveSnapshot((FrameworkElement)window.Content, options.SnapshotPath!);
            }
            finally
            {
                Shutdown();
            }
        };
        timer.Start();
    }

    private static void SaveSnapshot(FrameworkElement root, string path)
    {
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Developer/QA command-line switches. None of them touch the IKEMEN installation.</summary>
    private sealed class QaOptions
    {
        public NavPage? Page { get; private set; }
        public BrowserViewMode? View { get; private set; }
        public string? SnapshotPath { get; private set; }
        public double SnapshotDelaySeconds { get; private set; } = 8;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public Action<Window>? AfterLoad { get; private set; }

        public static QaOptions Parse(IEnumerable<string> args)
        {
            var o = new QaOptions();
            foreach (var arg in args)
            {
                var eq = arg.IndexOf('=');
                if (!arg.StartsWith("--", StringComparison.Ordinal) || eq < 0) continue;
                var key = arg[2..eq].ToLowerInvariant();
                var value = arg[(eq + 1)..];
                switch (key)
                {
                    case "page" when Enum.TryParse<NavPage>(value, true, out var page):
                        o.Page = page;
                        break;
                    case "view" when Enum.TryParse<BrowserViewMode>(value, true, out var view):
                        o.View = view;
                        break;
                    case "snapshot":
                        o.SnapshotPath = value;
                        break;
                    case "snapshot-delay" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d):
                        o.SnapshotDelaySeconds = Math.Clamp(d, 0.5, 120);
                        break;
                    case "size":
                        var parts = value.Split('x', 'X');
                        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                        {
                            o.Width = w;
                            o.Height = h;
                        }

                        break;
                }
            }

            return o;
        }
    }
}
