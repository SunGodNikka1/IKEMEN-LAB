using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using IKEMENLab.App.ViewModels;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>
/// The Play Combo bar lives in the Combos lens. Every test here builds the real XRayWindow, really shows it (a detached window never realises
/// its content tree, so searching it or measuring it proves nothing) and only then asserts. Window width is clamped by the window's own
/// MinWidth (1040), so layout is checked from there upward.
/// </summary>
[Collection(WpfUiCollection.Name)]
public class ComboPlaybackUiTests : IDisposable
{
    private readonly System.Collections.Generic.List<string> _cleanup = [];

    public void Dispose()
    {
        foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; set; } = new();
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    /// <summary>Ends with the engine never reaching a match: enough to produce a saved, route-scoped result without a real engine.</summary>
    private sealed class QuickRunner : IEngineRunner
    {
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout)
        {
            File.WriteAllText(sandbox.TracePath, string.Empty);
            return new EngineRunResult(0, false, null);
        }
    }

    /// <summary>Blocks like a live engine until cancelled, then throws like the real runner does.</summary>
    private sealed class LiveRunner : ICancellableEngineRunner
    {
        public readonly ManualResetEventSlim Entered = new();
        public string? SandboxRoot;
        public bool Started;
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
        {
            Started = true;
            SandboxRoot = sandbox.Root;
            Entered.Set();
            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            cancel.ThrowIfCancellationRequested();
            return new EngineRunResult(0, false, null);
        }
    }

    private string Temp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    private const string HeroCmd = """
        [Command]
        name = "x"
        command = x
        [Command]
        name = "y"
        command = y

        [Statedef -1]

        [State -1, x from neutral]
        type = ChangeState
        value = 200
        triggerall = command = "x"
        trigger1 = statetype = S && ctrl

        [State -1, y from neutral]
        type = ChangeState
        value = 210
        triggerall = command = "y"
        trigger1 = statetype = S && ctrl

        [State -1, x into y on hit]
        type = ChangeState
        value = 210
        triggerall = command = "y"
        trigger1 = stateno = 200 && movehit
        """;

    private const string HeroCns = """
        [Data]
        life = 1000

        [Statedef 200]
        type = S
        movetype = A
        anim = 200
        ctrl = 0

        [State 200, hit]
        type = HitDef
        trigger1 = AnimElem = 2
        attr = S, NA
        damage = 20

        [State 200, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 210]
        type = S
        movetype = A
        anim = 210
        ctrl = 0

        [State 210, hit]
        type = HitDef
        trigger1 = AnimElem = 2
        attr = S, NA
        damage = 30

        [State 210, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0
        """;

    /// <summary>A tiny install: a subject with three candidate routes (200, 210, 200→210), a dummy, a stage and an "engine" that carries the hook name.</summary>
    private (string Root, string Engine) Install()
    {
        var root = Temp("xray-ui-");
        void W(string rel, string text)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("Ikemen_GO.exe", "exe");
        W("data/select.def", "[Characters]\nkfm\n\n[ExtraStages]\nstages/ring.def\n");
        W("external/script/main.lua", "main()\n");
        W("chars/hero/hero.def", "[Info]\nname = Hero\n[Files]\ncmd = hero.cmd\ncns = hero.cns\nstcommon = common1.cns\n");
        W("chars/hero/hero.cmd", HeroCmd);
        W("chars/hero/hero.cns", HeroCns);
        W("chars/kfm/kfm.def", "[Info]\nname = kfm\n[Files]\ncns = kfm.cns\n");
        W("stages/ring.def", "[StageInfo]\n");
        var engine = Path.Combine(Temp("xray-eng-"), "xray_engine.exe");
        File.WriteAllText(engine, "binary " + PlaybackPreflight.HookName);
        return (root, engine);
    }

    private static CharacterEntry Hero() => new()
    {
        Id = "hero", DisplayName = "Hero", Name = "Hero", Author = "", VersionDate = "", DefPath = "chars/hero/hero.def", FolderPath = "chars/hero"
    };

    private XRayViewModel ViewModel(MemorySettings settings, string root, ComboPlaybackService? service = null) =>
        new(root, Hero(), "Hero", settings, service) { ActiveLens = XRayLensKind.Combos };

    private static XRayWindow ShowWindow(XRayViewModel vm, double width = 1200)
    {
        var window = new XRayWindow(vm);
        WpfHost.Show(window, width);
        return window;
    }

    /// <summary>Loads the index (the window's Loaded handler already started it) and runs Find routes.</summary>
    private static void LoadRoutes(XRayViewModel vm, int atLeast = 2)
    {
        Assert.True(WpfHost.PumpUntil(() => !vm.IsLoading), "The character index never finished loading.");
        vm.Combos.FindRoutesCommand.Execute(null);
        Assert.True(WpfHost.PumpUntil(() => vm.Combos.Routes.Count >= atLeast), $"Find routes produced {vm.Combos.Routes.Count} route(s); expected at least {atLeast}.");
    }

    private static T Named<T>(DependencyObject root, string automationName) where T : FrameworkElement =>
        Descendants(root).OfType<T>().SingleOrDefault(e => AutomationProperties.GetName(e) == automationName)
        ?? throw new InvalidOperationException($"The window renders no {typeof(T).Name} named '{automationName}'.");

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

    // ------------------------------------------------------------------ layout and wiring

    [Theory]
    [InlineData(1040)]
    [InlineData(1200)]
    [InlineData(1440)]
    public void PlayReplayAndSetupStayReachableInTheCombosLens(double width) => WpfHost.Run(() =>
    {
        var (root, _) = Install();
        var window = ShowWindow(ViewModel(new MemorySettings(), root), width);
        foreach (var name in new[] { "Play the selected combo in IKEMEN", "Replay the last combo", "Playback setup" })
        {
            var b = Named<Button>(window, name);
            Assert.True(b.IsVisible, $"'{name}' is not visible at window width {width}.");
            Assert.True(b.ActualWidth > 0 && b.ActualHeight > 0, $"'{name}' collapsed at window width {width}.");
            var right = b.TranslatePoint(new Point(b.ActualWidth, 0), window).X;
            Assert.True(right <= window.ActualWidth + 0.5, $"'{name}' is clipped past the window edge at width {width} (right edge {right:0}).");
        }
    });

    [Fact]
    public void PlayIsBoundToThePanelCommandAndDisabledWithoutASelectedRoute() => WpfHost.Run(() =>
    {
        var (root, _) = Install();
        var vm = ViewModel(new MemorySettings(), root);
        var window = ShowWindow(vm);
        var play = Named<Button>(window, "Play the selected combo in IKEMEN");
        Assert.Same(vm.Combos.Playback.PlayCommand, play.Command);
        Assert.False(play.Command!.CanExecute(null), "Play must be disabled until a candidate route is selected.");
    });

    [Fact]
    public void PlaybackSetupShowsEngineDllsDummyAndStageFieldsAndRemembersThem() => WpfHost.Run(() =>
    {
        var (root, _) = Install();
        var settings = new MemorySettings();
        var vm = ViewModel(settings, root);
        var window = ShowWindow(vm);
        Assert.Contains("X-Ray engine", vm.Combos.Playback.SetupSummary);      // nothing configured: the reason is already stated

        vm.Combos.Playback.ToggleSetupCommand.Execute(null);
        WpfHost.Layout(window, window.ActualWidth);
        foreach (var name in new[] { "X-Ray engine path", "Engine runtime DLL folder", "Dummy character", "Stage" })
        {
            var field = Named<TextBox>(window, name);
            Assert.True(field.IsVisible && field.ActualWidth > 0, $"The '{name}' field is not shown in Playback setup.");
        }

        vm.Combos.Playback.EnginePath = @"C:\engines\xray\Ikemen_GO.exe";
        vm.Combos.Playback.EngineDlls = @"C:\engines\xray\dlls";
        Assert.Equal(@"C:\engines\xray\Ikemen_GO.exe", settings.Current.XRayEnginePath);   // remembered
        Assert.Equal(@"C:\engines\xray\dlls", settings.Current.XRayEngineDlls);
    });

    // ------------------------------------------------------------------ a result belongs to the route that was played

    private (XRayViewModel Vm, XRayWindow Window) WindowWithRoutesAndAResult()
    {
        var (root, engine) = Install();
        var settings = new MemorySettings { Current = new AppSettings { XRayEnginePath = engine } };
        var service = new ComboPlaybackService(new QuickRunner(), Temp("xray-store-"), Temp("xray-sbx-"));
        var vm = ViewModel(settings, root, service);
        var window = ShowWindow(vm);
        LoadRoutes(vm);

        vm.Combos.SelectedRoute = vm.Combos.Routes[0];
        Assert.True(vm.Combos.Playback.PlayCommand.CanExecute(null), "Play should be enabled for a selected route.");
        vm.Combos.Playback.PlayCommand.Execute(null);
        Assert.True(WpfHost.PumpUntil(() => vm.Combos.Playback.HasStatus && !vm.Combos.Playback.IsBusy), "The playback never produced a result.");
        return (vm, window);
    }

    [Fact]
    public void AResultNeverAttachesItselfToADifferentSelectedRoute() => WpfHost.Run(() =>
    {
        var (vm, window) = WindowWithRoutesAndAResult();
        var playback = vm.Combos.Playback;
        var headline = playback.Headline;
        Assert.NotEmpty(headline);
        Assert.True(playback.HasResult);
        var banner = Named<TextBlock>(window, "Playback result");
        Assert.True(banner.IsVisible, "The result banner should show against the route that was played.");

        vm.Combos.SelectedRoute = vm.Combos.Routes[1];            // route B
        WpfHost.Layout(window, window.ActualWidth);

        Assert.False(playback.HasStatus, "Route A's result must not be shown against route B.");
        Assert.Equal(string.Empty, playback.Headline);
        Assert.False(playback.HasResult);
        Assert.False(playback.CanInspect);
        Assert.False(playback.ShowFailure);
        Assert.False(playback.InspectFailureCommand.CanExecute(null), "Inspect Failure must not act on a route it was not recorded for.");
        Assert.False(playback.ReplayCommand.CanExecute(null));
        Assert.False(playback.ViewTraceCommand.CanExecute(null));
        Assert.False(banner.IsVisible, "The banner is still visible against route B.");
        Assert.DoesNotContain(vm.Combos.RouteSteps, r => r.Title.StartsWith("✓") || r.Title.StartsWith("✗") || r.Title.StartsWith("·"));

        vm.Combos.SelectedRoute = vm.Combos.Routes[0];            // back to A: its result is again attributable
        WpfHost.Layout(window, window.ActualWidth);
        Assert.Equal(headline, playback.Headline);
        Assert.True(banner.IsVisible);
    });

    [Fact]
    public void AResultSurvivesAReSearchOnlyForTheSameRoute() => WpfHost.Run(() =>
    {
        var (vm, _) = WindowWithRoutesAndAResult();
        var played = vm.Combos.CurrentRoute!.Value.Route.Key;
        vm.Combos.MaxMoves = 1;                                    // a different search: the route list is rebuilt
        vm.Combos.FindRoutesCommand.Execute(null);
        Assert.True(WpfHost.PumpUntil(() => vm.Combos.Routes.Count > 0 && vm.Combos.Routes.All(r => r.Id != played) || vm.Combos.Routes.Any(r => r.Id == played)));
        WpfHost.Pump();
        // The result is shown exactly when the selected route is the one that was played — never otherwise.
        Assert.Equal(vm.Combos.CurrentRoute?.Route.Key == played, vm.Combos.Playback.HasResult);
    });

    // ------------------------------------------------------------------ closing the window owns the playback

    private (XRayViewModel Vm, XRayWindow Window, LiveRunner Runner, string Store, string Sandboxes) WindowWithLivePlayback()
    {
        var (root, engine) = Install();
        var runner = new LiveRunner();
        var store = Temp("xray-store-");
        var sandboxes = Temp("xray-sbx-");
        var settings = new MemorySettings { Current = new AppSettings { XRayEnginePath = engine } };
        var vm = ViewModel(settings, root, new ComboPlaybackService(runner, store, sandboxes));
        var window = ShowWindow(vm);
        LoadRoutes(vm);
        vm.Combos.SelectedRoute = vm.Combos.Routes[0];
        return (vm, window, runner, store, sandboxes);
    }

    [Fact]
    public void ClosingTheWindowDuringLivePlaybackStopsTheEngineAndCleansUpWithoutBlocking() => WpfHost.Run(() =>
    {
        var (vm, window, runner, store, sandboxes) = WindowWithLivePlayback();
        vm.Combos.Playback.PlayCommand.Execute(null);
        Assert.True(WpfHost.PumpUntil(() => runner.Entered.IsSet), "The engine never started.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        window.Close();                                            // Closed → XRayViewModel.Close: starts cleanup and returns; it never waits for the worker
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Closing the window stalled the UI thread for {sw.Elapsed.TotalSeconds:0.0} s.");

        Assert.True(WpfHost.PumpUntil(() => vm.ShutdownTask.IsCompleted), "The playback cleanup never completed.");
        Assert.True(vm.ShutdownTask.Result.Clean, vm.ShutdownTask.Result.Problem);
        Assert.False(Directory.Exists(runner.SandboxRoot), "The sandbox survived the window closing.");
        Assert.Empty(Directory.EnumerateDirectories(store));       // a cancelled playback saves no record
        Assert.Empty(Directory.EnumerateDirectories(sandboxes));
        var after = vm.Combos.Playback.Headline;
        WpfHost.Pump();
        Assert.Equal(after, vm.Combos.Playback.Headline);          // no late update reaches the closed window's view model
    });

    [Fact]
    public void ClosingWhileWorkerNotificationsAreStillPendingNeitherDeadlocksNorUpdatesTheClosedView() => WpfHost.Run(() =>
    {
        // Press Play and close WITHOUT pumping: the worker's phase notifications are queued on this dispatcher, unprocessed, when the window closes.
        // The old design had the worker invoke the UI thread synchronously while the UI thread waited for the worker (up to the 6 s timeout).
        var (vm, window, runner, store, sandboxes) = WindowWithLivePlayback();
        vm.Combos.Playback.PlayCommand.Execute(null);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        window.Close();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Closing stalled the UI thread for {sw.Elapsed.TotalSeconds:0.0} s.");

        var before = vm.Combos.Playback.Headline;
        Assert.True(WpfHost.PumpUntil(() => vm.ShutdownTask.IsCompleted), "The playback cleanup never completed (a worker waiting on the UI thread?).");
        WpfHost.Pump();                                            // the queued notifications run now — after the close
        Assert.True(vm.ShutdownTask.Result.Clean, vm.ShutdownTask.Result.Problem);
        Assert.Equal(before, vm.Combos.Playback.Headline);         // none of them mutated the closed view model
        Assert.Empty(Directory.EnumerateDirectories(store));       // pre-commit close: no record
        Assert.Empty(Directory.EnumerateDirectories(sandboxes));
        if (runner.Started) Assert.False(Directory.Exists(runner.SandboxRoot));
    });
}
