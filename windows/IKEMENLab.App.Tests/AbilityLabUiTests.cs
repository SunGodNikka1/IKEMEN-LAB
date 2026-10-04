using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
/// Phase 2 in the real X-Ray window: the Ability Atlas's Ability Lab (Play Ability, Preview State) on the window's one shared playback session.
/// A scripted runner answers the plan the sandbox really received, so the whole M3/M4 path (service → session → panel) runs; no engine is launched.
/// </summary>
[Collection(WpfUiCollection.Name)]
public class AbilityLabUiTests : IDisposable
{
    private readonly List<string> _cleanup = [];

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

    /// <summary>Writes a trace for whatever plan the sandbox got: P1 performs (or, for a preview, is forced into) the move, which hits for 20, then recovers.</summary>
    private sealed class ScriptedRunner : IEngineRunner
    {
        public readonly List<string> Plans = [];
        public ManualResetEventSlim? Gate;

        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout)
        {
            var lua = File.ReadAllText(Path.Combine(sandbox.Root, "external", "mods", "xray_plan.lua"));
            Plans.Add(lua);
            Gate?.Wait(TimeSpan.FromSeconds(20));
            var fingerprint = Regex.Match(lua, "fingerprint = \"([0-9A-F]+)\"").Groups[1].Value;
            var to = int.Parse(Regex.Match(lua, "toState = (\\d+)").Groups[1].Value);
            File.WriteAllText(sandbox.TracePath, lua.Contains("force = true") ? Preview(fingerprint, to) : Ability(fingerprint, to));
            return new EngineRunResult(0, false, null);
        }

        private static string Frame(long f, int p1, bool ctrl, int p2 = 0, string p2Move = "I", double life = 1000, int hit = 0) =>
            $"{{\"type\":\"frame\",\"frame\":{f},\"round\":1,\"p1\":{{\"state\":{p1},\"ctrl\":{(ctrl ? "true" : "false")},\"stateType\":\"S\",\"moveType\":\"{(p1 == 0 ? "I" : "A")}\",\"life\":1000,\"moveHit\":{hit},\"moveContact\":{hit},\"x\":0}}," +
            $"\"p2\":{{\"state\":{p2},\"ctrl\":{(p2 == 0 ? "true" : "false")},\"stateType\":\"S\",\"moveType\":\"{p2Move}\",\"life\":{life},\"moveHit\":0,\"moveContact\":0,\"x\":40}},\"distance\":40,\"distanceSource\":\"derived:p2.x-p1.x\"}}";

        private static string Meta(string fp) => $"{{\"type\":\"meta\",\"frame\":0,\"schema\":\"ikemenlab.xray.trace/0\",\"probeVersion\":\"ui-test\",\"capabilities\":{{}},\"hooks\":[\"hook:loop\"],\"planFingerprint\":\"{fp}\"}}";

        private static void Move(List<string> l, ref long f, int state)
        {
            for (var i = 0; i < 2; i++) l.Add(Frame(++f, state, false));
            for (var i = 0; i < 10; i++) l.Add(Frame(++f, state, false, 5000, "H", 980, 1));
            for (var i = 0; i < 2; i++) l.Add(Frame(++f, state, false, 0, "I", 980, 1));
            for (var i = 0; i < 30; i++) l.Add(Frame(++f, 0, true, 0, "I", 980));
        }

        private static string Ability(string fp, int to)
        {
            var l = new List<string> { Meta(fp), "{\"type\":\"driver\",\"frame\":0,\"event\":\"plan_start\"}" };
            long f = 0;
            for (var i = 0; i < 3; i++) l.Add(Frame(++f, 0, true));
            l.Add($"{{\"type\":\"input\",\"frame\":{f},\"player\":1,\"keys\":[\"x\"],\"step\":1,\"phase\":\"input\"}}");
            for (var i = 0; i < 2; i++) l.Add(Frame(++f, 0, true));
            l.Add($"{{\"type\":\"input\",\"frame\":{f},\"player\":1,\"keys\":[],\"step\":1,\"phase\":\"input\"}}");
            Move(l, ref f, to);
            l.Add($"{{\"type\":\"driver\",\"frame\":{f},\"event\":\"plan_complete\"}}");
            l.Add($"{{\"type\":\"end\",\"frame\":{f},\"reason\":\"planComplete\"}}");
            return string.Join("\n", l) + "\n";
        }

        private static string Preview(string fp, int to)
        {
            var l = new List<string> { Meta(fp), "{\"type\":\"driver\",\"frame\":0,\"event\":\"plan_start\"}" };
            long f = 0;
            for (var i = 0; i < 25; i++) l.Add(Frame(++f, 0, true));
            l.Add($"{{\"type\":\"driver\",\"frame\":{f},\"event\":\"force_applied\",\"step\":1,\"detail\":\"{to}\"}}");
            l.Add($"{{\"type\":\"driver\",\"frame\":{f},\"event\":\"tail_start\"}}");
            Move(l, ref f, to);
            l.Add($"{{\"type\":\"driver\",\"frame\":{f},\"event\":\"plan_complete\"}}");
            l.Add($"{{\"type\":\"end\",\"frame\":{f},\"reason\":\"planComplete\"}}");
            return string.Join("\n", l) + "\n";
        }
    }

    private string Temp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    private const string Cmd = """
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

        [State -1, y on hit only]
        type = ChangeState
        value = 210
        triggerall = command = "y"
        trigger1 = stateno = 200 && movehit
        """;

    private const string Cns = """
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
        """;

    private (string Root, string Engine) Install()
    {
        var root = Temp("xray-lab-");
        void W(string rel, string text)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("Ikemen_GO.exe", "exe");
        W("data/select.def", "[Characters]\nkfm\n\n[ExtraStages]\nstages/ring.def\n");
        W("external/script/main.lua", "main()\n");
        W("chars/hero/hero.def", "[Info]\nname = Hero\n[Files]\ncmd = hero.cmd\ncns = hero.cns\n");
        W("chars/hero/hero.cmd", Cmd);
        W("chars/hero/hero.cns", Cns);
        W("chars/kfm/kfm.def", "[Info]\nname = kfm\n[Files]\ncns = kfm.cns\n");
        W("stages/ring.def", "[StageInfo]\n");
        var engine = Path.Combine(Temp("xray-lab-eng-"), "xray_engine.exe");
        File.WriteAllText(engine, "binary " + PlaybackPreflight.HookName);
        return (root, engine);
    }

    private (XRayViewModel Vm, XRayWindow Window, ScriptedRunner Runner) Open(double width = 1200)
    {
        var (root, engine) = Install();
        var runner = new ScriptedRunner();
        var settings = new MemorySettings { Current = new AppSettings { XRayEnginePath = engine } };
        var entry = new CharacterEntry { Id = "hero", DisplayName = "Hero", Name = "Hero", Author = "", VersionDate = "", DefPath = "chars/hero/hero.def", FolderPath = "chars/hero" };
        var vm = new XRayViewModel(root, entry, "Hero", settings, new ComboPlaybackService(runner, Temp("xray-lab-store-"), Temp("xray-lab-sbx-")),
            new Core.XRay.Names.NameOverlayStore(Temp("xray-lab-names-")));
        var window = new XRayWindow(vm);
        WpfHost.Show(window, width);
        Assert.True(WpfHost.PumpUntil(() => !vm.IsLoading), "The character index never finished loading.");
        return (vm, window, runner);
    }

    private static T Named<T>(DependencyObject root, string automationName) where T : FrameworkElement =>
        Descendants(root).OfType<T>().SingleOrDefault(e => AutomationProperties.GetName(e) == automationName)
        ?? throw new InvalidOperationException($"The window renders no {typeof(T).Name} named '{automationName}'.");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    [Theory]
    [InlineData(1040)]
    [InlineData(1440)]
    public void TheAbilityLabSitsUnderTheAtlasBoundToTheSelectedAbility(double width) => WpfHost.Run(() =>
    {
        var (vm, window, _) = Open(width);
        try
        {
            vm.Select("ability:200");
            WpfHost.Layout(window, window.ActualWidth);
            Assert.Equal("ability:200", vm.Atlas.Lab.AbilityId);
            Assert.Contains("“x”", vm.Atlas.Lab.PathText);
            foreach (var name in new[] { "Play the selected ability in IKEMEN", "Preview the entry state (not proof)", "Open playback setup in the Combos lens" })
            {
                var b = Named<Button>(window, name);
                Assert.True(b.IsVisible && b.ActualWidth > 0, $"'{name}' is not shown at width {width}.");
                Assert.True(b.TranslatePoint(new Point(b.ActualWidth, 0), window).X <= window.ActualWidth + 0.5, $"'{name}' is clipped at width {width}.");
            }

            Assert.Same(vm.Atlas.Lab.PlayAbilityCommand, Named<Button>(window, "Play the selected ability in IKEMEN").Command);
            Assert.True(vm.Atlas.Lab.PlayAbilityCommand.CanExecute(null));

            vm.Select("ability:210");                                                 // only entered as a follow-up on hit
            WpfHost.Pump();
            Assert.StartsWith("Play Ability is not available", vm.Atlas.Lab.PathText);
            Assert.False(vm.Atlas.Lab.PlayAbilityCommand.CanExecute(null));
            Assert.True(vm.Atlas.Lab.PreviewStateCommand.CanExecute(null));           // a look is still possible, labelled not proof
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PlayAbilityShowsItsResultOnlyForThatAbilityAndNeverInTheCombosLens() => WpfHost.Run(() =>
    {
        var (vm, window, runner) = Open();
        try
        {
            vm.Select("ability:200");
            vm.Atlas.Lab.PlayAbilityCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.Atlas.Lab.HasStatus && !vm.Atlas.Lab.IsBusy && vm.PlaybackSession.State == PlaybackState.Finished), "Play Ability never produced a result.");
            WpfHost.Layout(window, window.ActualWidth);

            Assert.DoesNotContain("force", runner.Plans.Single());
            Assert.Equal("Verified", vm.Atlas.Lab.ResultKind);                          // performed (the banner's green), never "Preview"
            Assert.StartsWith("Performed · State 200 started via “x” · connected · 20 damage", vm.Atlas.Lab.Headline);
            Assert.Contains(vm.Atlas.Lab.ResultLines, l => l.StartsWith("Evidence: the move starting from its command"));
            Assert.True(Named<TextBlock>(window, "Ability playback result").IsVisible);
            Assert.Contains("IKEMEN Lab Play Ability diagnostic", vm.Atlas.Lab.DiagnosticText());

            vm.Select("ability:210");                                                 // another ability: its result is not shown there
            WpfHost.Pump();
            Assert.False(vm.Atlas.Lab.HasStatus);
            Assert.False(vm.Atlas.Lab.ViewTraceCommand.CanExecute(null));
            Assert.Equal(string.Empty, vm.Atlas.Lab.DiagnosticText());

            // The Combos lens: even the one-step route through the same command shows no verdict and no step marks.
            vm.Combos.MaxMoves = 1;
            vm.Combos.FindRoutesCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.Combos.Routes.Count > 0));
            var playedKey = (vm.PlaybackSession.LastJob as PlaybackRequest)?.Route.Key;
            var sameKey = vm.Combos.Routes.FirstOrDefault(r => r.Id == playedKey);
            Assert.NotNull(sameKey);                                                  // the search lists the very route Play Ability played
            vm.Combos.SelectedRoute = sameKey;
            WpfHost.Pump();
            Assert.False(vm.Combos.Playback.HasStatus);
            Assert.False(vm.Combos.Playback.HasResult);
            Assert.DoesNotContain(vm.Combos.RouteSteps, r => r.Title.StartsWith("✓") || r.Title.StartsWith("✗"));

            vm.Select("ability:200");
            WpfHost.Pump();
            Assert.True(vm.Atlas.Lab.HasStatus);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void APreviewIsLabelledNotProofAndNeverLooksLikeAPass() => WpfHost.Run(() =>
    {
        var (vm, window, runner) = Open();
        try
        {
            vm.Select("ability:210");
            vm.Atlas.Lab.PreviewStateCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.Atlas.Lab.HasStatus && !vm.Atlas.Lab.IsBusy && vm.PlaybackSession.State == PlaybackState.Finished), "The preview never produced a result.");

            Assert.Contains("force = true", runner.Plans.Single());
            Assert.Equal("Preview", vm.Atlas.Lab.ResultKind);
            Assert.Equal("◇", vm.Atlas.Lab.ResultGlyph);
            Assert.StartsWith("Preview (not proof) · State 210 forced", vm.Atlas.Lab.Headline);
            Assert.StartsWith("Preview — not proof.", vm.Atlas.Lab.ResultLines[0]);
            Assert.False(vm.Atlas.Lab.CanInspect);
            Assert.Equal(VerdictWaitKind.Preview, vm.PlaybackSession.VerdictForLatestAttempt().Kind);
            Assert.Contains("NOT PROOF", vm.Atlas.Lab.DiagnosticText());
            Assert.True(vm.Atlas.Lab.ViewTraceCommand.CanExecute(null));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void APressWithoutAnEngineSaysItDidNotStartAndRunsNothing() => WpfHost.Run(() =>
    {
        var (vm, window, runner) = Open();
        try
        {
            vm.Combos.Playback.EnginePath = string.Empty;                            // setup incomplete
            vm.Select("ability:200");
            vm.Atlas.Lab.PlayAbilityCommand.Execute(null);
            WpfHost.Pump();

            Assert.Empty(runner.Plans);
            Assert.Equal(AttemptState.PreflightRefused, vm.PlaybackSession.Attempt);
            Assert.StartsWith("Not started — playback setup is incomplete", vm.Atlas.Lab.Headline);
            Assert.Equal("Inconclusive", vm.Atlas.Lab.ResultKind);
            Assert.Empty(vm.Atlas.Lab.ResultLines);
            Assert.True(vm.Atlas.Lab.CopyDiagnosticCommand.CanExecute(null));
            Assert.StartsWith("IKEMEN Lab Play Ability diagnostic", vm.Atlas.Lab.DiagnosticText());
            Assert.Contains("kind: refused", vm.Atlas.Lab.DiagnosticText());
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OneEngineAtATimeAcrossPlayComboAndTheAbilityLab() => WpfHost.Run(() =>
    {
        var (vm, window, runner) = Open();
        try
        {
            runner.Gate = new ManualResetEventSlim(false);
            vm.Combos.FindRoutesCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.Combos.Routes.Count > 0));
            vm.Combos.SelectedRoute = vm.Combos.Routes[0];
            vm.Select("ability:200");

            vm.Atlas.Lab.PlayAbilityCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.PlaybackSession.IsBusy && runner.Plans.Count == 1), "The ability run never started.");
            Assert.False(vm.Combos.Playback.PlayCommand.CanExecute(null), "Play Combo must wait while the Ability Lab's engine runs.");
            Assert.False(vm.Atlas.Lab.PreviewStateCommand.CanExecute(null));
            Assert.True(vm.Atlas.Lab.CancelCommand.CanExecute(null));

            runner.Gate.Set();
            Assert.True(WpfHost.PumpUntil(() => !vm.PlaybackSession.IsBusy));
            Assert.True(vm.Combos.Playback.PlayCommand.CanExecute(null));
        }
        finally { window.Close(); }
    });
}
