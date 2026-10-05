using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IKEMENLab.App.ViewModels;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>
/// Phase 5 in the real X-Ray window: Watch &amp; Ask watches a match through the window's one session, shows the recognised behavior in plain words with its
/// evidence badge, explains a moment without claiming a cause, and uses the user's names. The engine answers watched matches with the behavior tests'
/// synthetic knockdown chase, stamped with the hash of the engine copy the sandbox really got.
/// </summary>
[Collection(WpfUiCollection.Name)]
public class WatchAskUiTests : IDisposable
{
    private readonly global::IKEMENLab.Tests.XRayFixtures _fx = new();
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        _fx.Dispose();
        foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private string Temp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; set; } = new();
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    private sealed class WatchRunner : ICancellableEngineRunner
    {
        public bool Block;
        public readonly ManualResetEventSlim Entered = new();
        public readonly List<string> Sandboxes = [];
        /// <summary>What the match records, given the engine copy's sha256.</summary>
        public Func<string, string> Trace = sha => global::IKEMENLab.Tests.BehaviorTraces.KnockdownChase().Jsonl(sha);
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
        {
            Sandboxes.Add(sandbox.Root);
            Entered.Set();
            if (Block) { cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)); cancel.ThrowIfCancellationRequested(); }
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sandbox.ExePath)));
            File.WriteAllText(sandbox.TracePath, Trace(sha));
            return new EngineRunResult(0, false, null);
        }
    }

    private (XRayViewModel Vm, XRayWindow Window, WatchRunner Runner, string WatchRoot) Open(double width = 1280)
    {
        var root = _fx.Root;
        void W(string rel, string text)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("Ikemen_GO.exe", "exe");
        W("data/select.def", "[Characters]\nComboGuy\n\n[ExtraStages]\nstages/ring.def\n");
        W("external/script/main.lua", "main()\n");
        W("stages/ring.def", "[StageInfo]\n");
        var engine = Path.Combine(Temp("xray-watch-eng-"), "xray_engine.exe");
        File.WriteAllText(engine, "binary " + PlaybackPreflight.HookName);
        var runner = new WatchRunner();
        var watchRoot = Temp("xray-watch-store-");
        var entry = new CharacterEntry { Id = "ComboGuy", DisplayName = "Combo Guy", Name = "Combo Guy", Author = "", VersionDate = "", DefPath = "chars/ComboGuy/ComboGuy.def", FolderPath = "chars/ComboGuy" };
        var vm = new XRayViewModel(root, entry, "Combo Guy", new MemorySettings { Current = new AppSettings { XRayEnginePath = engine, XRayDummy = "IkemenGuy", XRayStage = "stages/ring.def" } },
            new ComboPlaybackService(runner, Temp("xray-watch-pb-"), Temp("xray-watch-sbx-")), new NameOverlayStore(Temp("xray-watch-names-")),
            new SequenceStore(Temp("xray-watch-seqs-")), new ExperimentStore(Temp("xray-watch-exp-")), new BehaviorStore(watchRoot));
        var window = new XRayWindow(vm);
        WpfHost.Show(window, width);
        Assert.True(WpfHost.PumpUntil(() => !vm.IsLoading), "The character index never finished loading.");
        vm.ActiveLens = XRayLensKind.Watch;
        WpfHost.Layout(window, width);
        return (vm, window, runner, watchRoot);
    }

    private static T Named<T>(DependencyObject root, string automationName) where T : FrameworkElement =>
        Descendants(root).OfType<T>().FirstOrDefault(e => AutomationProperties.GetName(e) == automationName && e.IsVisible)
        ?? throw new InvalidOperationException($"The window renders no visible {typeof(T).Name} named '{automationName}'.");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    [Fact]
    public void WatchingAMatchShowsTheBehaviorWithItsSituationActionOutcomeAndEvidenceBadge() => WpfHost.Run(() =>
    {
        var (vm, window, runner, watchRoot) = Open();
        try
        {
            var lens = vm.WatchAsk;
            Assert.Equal("Watch a match to see its behavior.", lens.CurrentBehavior);
            Assert.All(lens.Cards, c => Assert.Equal("Not seen", c.Level));              // ComboGuy has no AI rules and nothing was watched yet
            Assert.Equal(10, lens.Cards.Count);

            Named<Button>(window, "Watch a match").Command.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.PlaybackSession.State == PlaybackState.Finished && lens.Runs.Count == 1), "The watched match never finished.");
            WpfHost.Layout(window, window.ActualWidth);

            Assert.StartsWith("Watched 60 s (1 round) vs IkemenGuy on ring (opponent AI 4): ", lens.Headline);
            Assert.Equal("Knockdown Chase", lens.CurrentBehavior);
            Assert.Equal("Observed", lens.CurrentBadge);
            Assert.Equal("Enemy knocked down (falling); Enemy lying on the ground", lens.CurrentSituation);
            Assert.Equal("Moves toward them: State 20 → Distance 120 → 84 → State 230 at 84", lens.CurrentAction);
            Assert.Equal("Connected", lens.CurrentOutcome);
            Assert.Equal("Knockdown Chase", Named<TextBlock>(window, "Likely behavior").Text);
            Assert.Equal("Observed", Named<TextBlock>(window, "Evidence badge").Text);
            Assert.Contains(lens.Timeline, t => t.Text == "▶ Knockdown Chase begins");
            Assert.Contains(lens.Timeline, t => t.Text == "Combo Guy moves in: State 20 · distance 120 → 84");
            Assert.DoesNotContain(lens.Timeline, t => t.Frame > 120);                    // the default view stays around the episode
            lens.ShowAllEvents = true;
            Assert.Contains(lens.Timeline, t => t.Text.StartsWith("Combo Guy: State 1000", StringComparison.Ordinal));

            Assert.All(lens.Cards.Take(2), c => Assert.Equal("Observed", c.Level));     // the observed behaviors come first (Ground Follow-Up, Knockdown Chase)
            var card = lens.Cards.Single(c => c.Name == "Knockdown Chase");
            Assert.Equal("1 episode(s) · 1 run(s) · 1 setup(s)", card.Counts);
            Assert.Contains(card.Links, l => l.Id == "state:20");
            Assert.Equal("Not seen", lens.Cards.Single(c => c.Name == "Anti-Air").Level);

            // Why: the observed context and the matching pattern; never a cause.
            lens.WhyAt(33);
            Assert.True(lens.HasWhy);
            Assert.Contains("This matches the Knockdown Chase and Ground Follow-Up patterns.", lens.WhySummary);
            Assert.Equal(BehaviorWhy.NoAttribution, lens.WhyCause);
            WpfHost.Layout(window, window.ActualWidth);
            Assert.Equal(BehaviorWhy.NoAttribution, Named<TextBlock>(window, "Why cause").Text);

            // The run is in the watched-run store, never in the playback history or the experiment store.
            Assert.Single(Directory.EnumerateDirectories(watchRoot));
            Assert.Empty(Directory.EnumerateDirectories(vm.PlaybackService.StoreRoot));
            Assert.All(runner.Sandboxes, s => Assert.False(Directory.Exists(s)));

            // A screenshot of the lens for the record (not asserted).
            Render(window, Path.Combine(Path.GetTempPath(), "ikemenlab-watch-ask-uitest.png"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RenamedMovesAppearInTheBehaviorView() => WpfHost.Run(() =>
    {
        var (vm, window, _, _) = Open();
        try
        {
            vm.WatchAsk.WatchCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.PlaybackSession.State == PlaybackState.Finished && vm.WatchAsk.Runs.Count == 1));
            vm.Select("state:20");
            vm.RenameText = "Walk Forward";
            vm.RenameCommand.Execute(null);
            WpfHost.Pump();
            Assert.Equal("Moves toward them: Walk Forward (20) → Distance 120 → 84 → State 230 at 84", vm.WatchAsk.CurrentAction);
            Assert.Contains(vm.WatchAsk.Timeline, t => t.Text == "Combo Guy moves in: Walk Forward (20) · distance 120 → 84");
            Assert.Contains(vm.WatchAsk.Cards.Single(c => c.Name == "Knockdown Chase").Links, l => l.Text.StartsWith("Walk Forward (20)", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AMatchTheEngineSaysWasNotOnTheAiIsShownButIsNeverAiEvidence() => WpfHost.Run(() =>
    {
        var (vm, window, runner, _) = Open();
        try
        {
            var human = new global::IKEMENLab.Tests.BehaviorTraces.Tb();
            foreach (var f in global::IKEMENLab.Tests.BehaviorTraces.KnockdownChase().Frames) human.Frames.Add(f with { P1 = f.P1 with { AiLevel = 0 } });
            runner.Trace = sha => human.Jsonl(sha);
            vm.WatchAsk.WatchCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.PlaybackSession.State == PlaybackState.Finished && vm.WatchAsk.Runs.Count == 1));
            WpfHost.Layout(window, window.ActualWidth);

            Assert.Equal("Knockdown Chase", vm.WatchAsk.CurrentBehavior);                 // what happened is still shown…
            Assert.Equal("Not AI evidence", vm.WatchAsk.CurrentBadge);                    // …but never as evidence about the AI
            Assert.Equal("Not AI evidence", Named<TextBlock>(window, "Evidence badge").Text);
            Assert.All(vm.WatchAsk.Cards, c => Assert.Equal("Not seen", c.Level));
            Assert.Contains("control: not on the AI (the engine reported AI level 0)", vm.WatchAsk.DetailsText);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WatchingCanBeCancelledAndLeavesNothingBehind() => WpfHost.Run(() =>
    {
        var (vm, window, runner, watchRoot) = Open();
        try
        {
            runner.Block = true;
            vm.WatchAsk.WatchCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => runner.Entered.IsSet && vm.WatchAsk.IsBusy));
            Assert.True(vm.WatchAsk.CancelCommand.CanExecute(null));
            vm.WatchAsk.CancelCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.PlaybackSession.State == PlaybackState.Cancelled));
            Assert.Equal("Watching cancelled.", vm.WatchAsk.Headline);
            Assert.Empty(vm.WatchAsk.Runs);
            Assert.Empty(Directory.EnumerateDirectories(watchRoot));
            Assert.All(runner.Sandboxes, s => Assert.False(Directory.Exists(s)));
        }
        finally { window.Close(); }
    });

    private static void Render(Window window, string path)
    {
        var element = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
