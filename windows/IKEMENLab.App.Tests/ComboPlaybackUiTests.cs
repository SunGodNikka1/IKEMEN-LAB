using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IKEMENLab.App.ViewModels;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Settings;
using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>
/// The Play Combo bar lives in the Combos lens. These tests measure the real XRayWindow view (not a stub) so a bar that exists but is
/// clipped, unbound or never shown fails here — the same lesson as the character-detail X-Ray button.
/// </summary>
[Collection(WpfUiCollection.Name)]
public class ComboPlaybackUiTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; set; } = new();
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(() =>
            {
                try { EnsureAppResources(); body(); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!done.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("WPF test body timed out.");
        if (failure is not null) throw failure;
    }

    private static readonly object AppLock = new();

    private static void EnsureAppResources()
    {
        lock (AppLock)
        {
        if (Application.Current is not null) return;
        var app = new Application();
        foreach (var uri in new[]
        {
            "pack://application:,,,/IKEMENLab;component/Themes/Colors.Dark.xaml",
            "pack://application:,,,/IKEMENLab;component/Themes/Controls.xaml",
        })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uri) });
        }
    }

    private static string TempInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "xray-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chars", "hero"));
        File.WriteAllText(Path.Combine(root, "chars", "hero", "hero.def"), "[Info]\nname = Hero\n[Files]\ncmd = hero.cmd\ncns = hero.cns\n");
        File.WriteAllText(Path.Combine(root, "chars", "hero", "hero.cmd"), "[Command]\nname = \"x\"\ncommand = x\n");
        File.WriteAllText(Path.Combine(root, "chars", "hero", "hero.cns"), "[Statedef 200]\ntype = S\n");
        return root;
        }

    private static XRayWindow Window(MemorySettings settings, string root)
    {
        var entry = new CharacterEntry
        {
            Id = "hero", DisplayName = "Hero", Name = "Hero", Author = "", VersionDate = "", DefPath = "chars/hero/hero.def", FolderPath = "chars/hero"
        };
        var vm = new XRayViewModel(root, entry, "Hero", settings);
        vm.ActiveLens = XRayLensKind.Combos;
        return new XRayWindow(vm);
    }

    private static Button[] PlaybackButtons(XRayWindow window) =>
        Descendants(window).OfType<Button>().Where(b => System.Windows.Automation.AutomationProperties.GetName(b) is
            "Play the selected combo in IKEMEN" or "Replay the last combo" or "Playback setup").ToArray();

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static void Layout(FrameworkElement fe, double width)
    {
        fe.Width = width;
        fe.Measure(new Size(width, 1400));
        fe.Arrange(new Rect(0, 0, width, 1400));
        fe.UpdateLayout();
    }

    [Theory]
    [InlineData(700)]
    [InlineData(1000)]
    [InlineData(1400)]
    public void PlayReplayAndSetupStayReachableInTheCombosLens(double width)
    {
        OnSta(() =>
        {
            var window = Window(new MemorySettings(), TempInstall());
            Layout(window, width);
            var buttons = PlaybackButtons(window);
            Assert.Equal(3, buttons.Length);
            foreach (var b in buttons)
            {
                var name = System.Windows.Automation.AutomationProperties.GetName(b);
                Assert.True(b.ActualWidth > 0 && b.ActualHeight > 0, $"'{name}' collapsed at window width {width}.");
                var right = b.TranslatePoint(new Point(b.ActualWidth, 0), window).X;
                Assert.True(right <= width + 0.5, $"'{name}' is clipped past the window edge at width {width} (right edge {right:0}).");
            }
        });
    }

    [Fact]
    public void PlayIsBoundToThePanelCommandAndDisabledWithoutASelectedRoute()
    {
        OnSta(() =>
        {
            var window = Window(new MemorySettings(), TempInstall());
            Layout(window, 1000);
            var play = PlaybackButtons(window).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Play the selected combo in IKEMEN");
            var vm = (XRayViewModel)window.DataContext;
            Assert.Same(vm.Combos.Playback.PlayCommand, play.Command);
            Assert.False(play.Command!.CanExecute(null), "Play must be disabled until a candidate route is selected.");
        });
    }

    [Fact]
    public void PlaybackSetupOpensWithTheEngineFieldAndExplainsWhatIsMissing()
    {
        OnSta(() =>
        {
            var settings = new MemorySettings();
            var window = Window(settings, TempInstall());
            Layout(window, 1000);
            var vm = (XRayViewModel)window.DataContext;
            Assert.Contains("X-Ray engine", vm.Combos.Playback.SetupSummary);   // no engine configured yet: the reason is already stated

            vm.Combos.Playback.ToggleSetupCommand.Execute(null);
            Layout(window, 1000);
            var field = Descendants(window).OfType<TextBox>()
                .SingleOrDefault(t => System.Windows.Automation.AutomationProperties.GetName(t) == "X-Ray engine path")
                ?? throw new InvalidOperationException("Playback setup shows no engine path field.");
            Assert.True(field.ActualWidth > 0);

            vm.Combos.Playback.EnginePath = @"C:\engines\xray\Ikemen_GO.exe";
            Assert.Equal(@"C:\engines\xray\Ikemen_GO.exe", settings.Current.XRayEnginePath);   // the choice is remembered
        });
    }
}

