using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using IKEMENLab.App.ViewModels;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>
/// Phase 3 in the real X-Ray window: Try Follow-Up opens the Sequence Lab; steps are edited, reordered and removed; Run plays the whole sequence through
/// the window's one session into the isolated experiment store. The runner answers with the Core tests' mock-engine traces (the real driver and probe
/// run on the same ComboGuy plans), re-identified to the plan the sandbox really received.
/// </summary>
[Collection(WpfUiCollection.Name)]
public class SequenceLabUiTests : IDisposable
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

    private sealed class FixtureRunner : ICancellableEngineRunner
    {
        public string Scenario = "seq_true";
        public bool BlockUntilCancelled;
        public readonly ManualResetEventSlim Entered = new();
        public readonly List<string> Sandboxes = [];
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
        {
            Sandboxes.Add(sandbox.Root);
            Entered.Set();
            if (BlockUntilCancelled) { cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)); cancel.ThrowIfCancellationRequested(); }
            var lua = File.ReadAllText(Path.Combine(sandbox.Root, "external", "mods", "xray_plan.lua"));
            var fingerprint = Regex.Match(lua, "fingerprint = \"([0-9A-F]+)\"").Groups[1].Value;
            var route = Regex.Match(lua, "route = \"([^\"]+)\"").Groups[1].Value;
            var trace = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"xray_driver_mock_{Scenario}.jsonl"));
            // Re-identify the recording to the plan the sandbox really got: its fingerprint and its route key (the sequence id and version).
            trace = Regex.Replace(trace, "\"planFingerprint\":\"[0-9A-F]*\"", "\"planFingerprint\":\"" + fingerprint + "\"").Replace("sequence:seq-test@v1", route);
            File.WriteAllText(sandbox.TracePath, trace);
            return new EngineRunResult(0, false, null);
        }
    }

    private (XRayViewModel Vm, XRayWindow Window, FixtureRunner Runner, string PlaybackStore, string ExperimentRoot, string Sandboxes) Open(double width = 1200)
    {
        var root = _fx.Root;
        void W(string rel, string text)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("Ikemen_GO.exe", "exe");
        W("data/select.def", "[Characters]\nkfm\n\n[ExtraStages]\nstages/ring.def\n");
        W("external/script/main.lua", "main()\n");
        W("stages/ring.def", "[StageInfo]\n");
        var engine = Path.Combine(Temp("xray-seqlab-eng-"), "xray_engine.exe");
        File.WriteAllText(engine, "binary " + PlaybackPreflight.HookName);

        var runner = new FixtureRunner();
        var playbackStore = Temp("xray-seqlab-playback-");
        var experiments = Temp("xray-seqlab-exp-");
        var sandboxes = Temp("xray-seqlab-sbx-");
        var entry = new CharacterEntry { Id = "ComboGuy", DisplayName = "Combo Guy", Name = "Combo Guy", Author = "", VersionDate = "", DefPath = "chars/ComboGuy/ComboGuy.def", FolderPath = "chars/ComboGuy" };
        var vm = new XRayViewModel(root, entry, "Combo Guy", new MemorySettings { Current = new AppSettings { XRayEnginePath = engine } },
            new ComboPlaybackService(runner, playbackStore, sandboxes), new NameOverlayStore(Temp("xray-seqlab-names-")),
            new SequenceStore(Temp("xray-seqlab-seqs-")), new ExperimentStore(experiments));
        var window = new XRayWindow(vm);
        WpfHost.Show(window, width);
        Assert.True(WpfHost.PumpUntil(() => !vm.IsLoading), "The character index never finished loading.");
        return (vm, window, runner, playbackStore, experiments, sandboxes);
    }

    private static T Named<T>(DependencyObject root, string automationName) where T : FrameworkElement =>
        Descendants(root).OfType<T>().FirstOrDefault(e => AutomationProperties.GetName(e) == automationName && e.IsVisible)
        ?? throw new InvalidOperationException($"The window renders no visible {typeof(T).Name} named '{automationName}'.");

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

    [Fact]
    public void TryFollowUpStartsASequenceWhoseStepsCanBeEditedReorderedAndRemoved() => WpfHost.Run(() =>
    {
        var (vm, window, _, _, _, _) = Open(1040);
        try
        {
            vm.Select("ability:200");
            Assert.True(vm.Atlas.Lab.TryFollowUpCommand.CanExecute(null));
            vm.Atlas.Lab.TryFollowUpCommand.Execute(null);
            WpfHost.Layout(window, window.ActualWidth);
            var lab = vm.SequenceLab;
            Assert.Equal(XRayLensKind.Sequences, vm.ActiveLens);
            var first = Assert.Single(lab.Steps);
            Assert.Equal(vm.Index!.NameOf("ability:200"), first.Label);
            Assert.StartsWith("From neutral, press “x”", first.How);

            lab.DistanceToAdd = "40";
            lab.AddChaseCommand.Execute(null);
            lab.AbilityToAdd = lab.Abilities.Single(a => a.Id == "ability:220");
            lab.AddAbilityCommand.Execute(null);
            WpfHost.Layout(window, window.ActualWidth);
            Assert.Equal(3, lab.Steps.Count);
            Assert.StartsWith("Ready:", lab.PlanText);
            Assert.StartsWith("Once you can act, walk forward until within 40", lab.Steps[1].How);
            Assert.StartsWith("Once you can act, press “z”", lab.Steps[2].How);

            lab.MoveUpCommand.Execute(lab.Steps[2]);                                       // now: 200 → 220 → chase
            Assert.Equal("ability:220", lab.Steps[1].AbilityId);
            Assert.StartsWith("Cancel", lab.Steps[1].How);                                   // straight after 200 it is the real hit-confirm cancel
            lab.RemoveStepCommand.Execute(lab.Steps[2]);
            Assert.Equal(2, lab.Steps.Count);

            lab.FramesToAdd = "0";
            lab.AddWaitCommand.Execute(null);
            Assert.Contains("Frames must be", lab.Steps[2].Problem);
            Assert.False(lab.Run1Command.CanExecute(null));
            lab.Steps[2].FramesText = "6";
            Assert.False(lab.Steps[2].HasProblem);
            Assert.True(lab.Run1Command.CanExecute(null));

            foreach (var name in new[] { "Run the sequence once", "Add chase", "Remove step", "Add wait", "Save sequence" })
                Assert.True(Named<Button>(window, name).ActualWidth > 0, name);
            Assert.Contains("×50 launches IKEMEN 50 time(s)", lab.CostText);
            Assert.False(lab.Run50Command.CanExecute(null));                                // ×50 needs its confirmation
            lab.Confirm50 = true;
            Assert.True(lab.Run50Command.CanExecute(null));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RunShowsTheVerdictPerStepOnlyForTheExactVersionAndNeverInCombos() => WpfHost.Run(() =>
    {
        var (vm, window, runner, playbackStore, experiments, sandboxes) = Open();
        try
        {
            var lab = vm.SequenceLab;
            vm.ActiveLens = XRayLensKind.Sequences;
            lab.LoadSpec("200 > 210", "Jab into kick");
            runner.Scenario = "seq_true";
            lab.Run1Command.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => !vm.PlaybackSession.IsBusy && vm.PlaybackSession.State == PlaybackState.Finished), "The sequence never finished.");
            WpfHost.Layout(window, window.ActualWidth);

            Assert.True(lab.ResultKind == "TrueCombo", $"{lab.ResultKind}: {lab.Headline} | session {vm.PlaybackSession.State} {vm.PlaybackSession.Error} | {lab.DetailsText}");
            Assert.StartsWith("True combo: all 2 attacks connected", lab.Headline);
            Assert.All(lab.Steps, s => Assert.Equal("✓", s.Glyph));
            Assert.StartsWith("Done — connected at frame", lab.Steps[1].Result);
            Assert.EndsWith("@v1", lab.SavedKey);                                            // a run always belongs to a saved version
            Assert.True(Named<TextBlock>(window, "Sequence result").IsVisible);
            Assert.Contains("seq-", lab.DetailsText);
            Assert.Single(Directory.GetDirectories(experiments));
            Assert.Empty(Directory.GetDirectories(playbackStore));                          // the playback history is untouched
            Assert.Empty(Directory.GetDirectories(sandboxes));
            Assert.False(vm.Combos.Playback.HasStatus);
            Assert.False(vm.Combos.Playback.HasResult);

            lab.FramesToAdd = "4";
            lab.AddWaitCommand.Execute(null);                                                // an edited draft is another version: no result for it
            Assert.Null(lab.SavedKey);
            Assert.Equal(string.Empty, lab.Headline);
            Assert.Empty(lab.ResultLines);
            Assert.Contains("edited", lab.VersionText);

            runner.Scenario = "seq_gap";                                                      // a different sequence: not a combo
            lab.LoadSpec("200 > walk:5 > 220", "Jab, walk, heavy");
            lab.Run1Command.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => !vm.PlaybackSession.IsBusy && vm.PlaybackSession.State == PlaybackState.Finished));
            Assert.Equal("Connected", lab.ResultKind);
            Assert.StartsWith("Connected sequence", lab.Headline);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ASequenceRunCanBeCancelledAndLeavesNothingBehind() => WpfHost.Run(() =>
    {
        var (vm, window, runner, playbackStore, experiments, sandboxes) = Open();
        try
        {
            var lab = vm.SequenceLab;
            lab.LoadSpec("200 > chase:40 > 220");
            runner.BlockUntilCancelled = true;
            lab.Run10Command.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.PlaybackSession.IsBusy && runner.Entered.IsSet), "The run never started.");
            Assert.True(lab.CancelCommand.CanExecute(null));
            Assert.False(vm.Combos.Playback.PlayCommand.CanExecute(null));                   // one engine at a time
            lab.CancelCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => !vm.PlaybackSession.IsBusy), "The cancelled run never stopped.");
            Assert.Equal(PlaybackState.Cancelled, vm.PlaybackSession.State);
            Assert.Equal("Run cancelled.", lab.Headline);
            Assert.All(runner.Sandboxes, s => Assert.False(Directory.Exists(s)));
            Assert.Empty(Directory.GetDirectories(experiments));                            // no finished trial: no experiment kept
            Assert.Empty(Directory.GetDirectories(playbackStore));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ScriptedQaRunsUseIsolatedStoresAndNeverWriteTheUsersSettings()
    {
        var source = new MemorySettings { Current = new AppSettings { XRayDummy = "kfm" } };
        var copyPath = Path.Combine(Temp("xray-qa-settings-"), "settings.json");
        var store = new CopyOnWriteSettingsStore(source, copyPath);
        Assert.Equal("kfm", store.Load().XRayDummy);                                   // reads the real settings until it has its own copy
        store.Save(new AppSettings { XRayDummy = "zed" });
        Assert.Equal("kfm", source.Current.XRayDummy);                                 // the real settings are never written
        Assert.Equal("zed", store.Load().XRayDummy);
        Assert.True(File.Exists(copyPath));

        var iso = Temp("xray-qa-iso-");
        XRayViewModel.IsolatedDataRoot = iso;
        try
        {
            WpfHost.Run(() =>
            {
                var entry = new CharacterEntry { Id = "ComboGuy", DisplayName = "Combo Guy", Name = "Combo Guy", Author = "", VersionDate = "", DefPath = "chars/ComboGuy/ComboGuy.def", FolderPath = "chars/ComboGuy" };
                var vm = new XRayViewModel(_fx.Root, entry, "Combo Guy");
                Assert.StartsWith(iso, vm.PlaybackService.StoreRoot);
                Assert.StartsWith(iso, vm.ExperimentStore.Root);
                Assert.StartsWith(iso, vm.SequenceStore.Directory);
                Assert.StartsWith(iso, vm.NameStore.Directory);
                Assert.StartsWith(iso, vm.BehaviorStore.Root);                              // Phase 5: watched runs too
                Assert.IsType<CopyOnWriteSettingsStore>(vm.Settings);
            });
        }
        finally
        {
            XRayViewModel.IsolatedDataRoot = null;
        }
    }

    [Fact]
    public void RenamedAbilitiesAppearInTheStepCardsAndThePicker() => WpfHost.Run(() =>
    {
        var (vm, window, _, _, _, _) = Open();
        try
        {
            vm.SequenceLab.LoadSpec("200 > chase:40 > 220");
            vm.Select("ability:200");
            vm.RenameText = "Jab";
            vm.RenameCommand.Execute(null);
            WpfHost.Pump();
            var lab = vm.SequenceLab;
            Assert.Equal("Jab", lab.Steps[0].Label);
            Assert.Contains(lab.Abilities, a => a.Id == "ability:200" && a.Name == "Jab");
            Assert.StartsWith("Ready: Jab → Chase until within 40 →", lab.PlanText);
        }
        finally { window.Close(); }
    });
}
