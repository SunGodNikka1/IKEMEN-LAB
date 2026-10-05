using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using IKEMENLab.App.Services;
using IKEMENLab.App.ViewModels;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Director;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>
/// Phase 6 in the real X-Ray window: teach a Knockdown Chase from a Sequence Lab experiment, save it, test it (every run in a sandbox, from the working copy),
/// ask exactly why it attacked, and keep approval and deployment separate — the deployment through the injected SafeMutation service, into the fixture install.
/// </summary>
[Collection(WpfUiCollection.Name)]
public class DirectorUiTests : IDisposable
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

    /// <summary>A Teach AI fixture gets a Director trace; a sequence the mock sequence trace; a one-step plan the mock Play Ability trace; a watch a knockdown chase.</summary>
    private sealed class Runner : ICancellableEngineRunner
    {
        public readonly List<string> Sandboxes = [];
        public readonly List<string> Generated = [];
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
        {
            Sandboxes.Add(sandbox.Root);
            Generated.Add(string.Join(",", Directory.EnumerateFiles(Path.Combine(sandbox.Root, "chars"), CharacterFacts.GeneratedFile, SearchOption.AllDirectories).Select(Path.GetFileName)));
            var planPath = Path.Combine(sandbox.Root, "external", "mods", "xray_plan.lua");
            var lua = File.Exists(planPath) ? File.ReadAllText(planPath) : null;
            string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
            var fp = lua is null ? string.Empty : Regex.Match(lua, "fingerprint = \"([0-9A-F]+)\"").Groups[1].Value;
            var route = lua is null ? string.Empty : Regex.Match(lua, "route = \"([^\"]+)\"").Groups[1].Value;
            // A watched match: the behavior tests' knockdown chase, its follow-up made x (State 200, an ability of ComboGuy with runtime proof).
            var trace = lua is null ? global::IKEMENLab.Tests.BehaviorTraces.KnockdownChase().Jsonl(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sandbox.ExePath))))
                    .Replace("\"state\":230", "\"state\":200", StringComparison.Ordinal)
                : lua.Contains("action = \"handover\"", StringComparison.Ordinal) ? global::IKEMENLab.Tests.DirectorFixtures.Trace(lua.Contains("opponentAi = ", StringComparison.Ordinal) ? "recover" : "connect", fp)
                : route.StartsWith("sequence:", StringComparison.Ordinal) ? Fixture("xray_driver_mock_seq_true.jsonl").Replace("sequence:seq-test@v1", route)
                : Fixture("xray_driver_mock_ability.jsonl");
            File.WriteAllText(sandbox.TracePath, Regex.Replace(trace, "\"planFingerprint\":\"[0-9A-F]*\"", "\"planFingerprint\":\"" + fp + "\""));
            return new EngineRunResult(0, false, null);
        }
    }

    private sealed record Opened(XRayViewModel Vm, XRayWindow Window, Runner Runner, ExperimentSummary Experiment, string Installed, string DirectorRoot);

    private Opened Open(double width = 1280)
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
        File.AppendAllText(Path.Combine(root, "chars", "ComboGuy", "ComboGuy.air"), global::IKEMENLab.Tests.DirectorFixtures.Anims);
        var engine = Path.Combine(Temp("xray-dir-eng-"), "xray_engine.exe");
        File.WriteAllText(engine, "binary " + PlaybackPreflight.HookName);
        var entry = new CharacterEntry { Id = "ComboGuy", DisplayName = "Combo Guy", Name = "Combo Guy", Author = "", VersionDate = "", DefPath = "chars/ComboGuy/ComboGuy.def", FolderPath = "chars/ComboGuy" };
        var experiments = new ExperimentStore(Temp("xray-dir-exp-"));
        var experiment = global::IKEMENLab.Tests.DirectorFixtures.Experiment(CharacterSemanticIndexer.Build(root, entry), experiments, engine: EngineIdentity.Sha256(engine)!);
        var runner = new Runner();
        var directorRoot = Temp("xray-dir-root-");
        var vm = new XRayViewModel(root, entry, "Combo Guy", new MemorySettings { Current = new AppSettings { XRayEnginePath = engine, XRayDummy = "IkemenGuy", XRayStage = "stages/ring.def" } },
            new ComboPlaybackService(runner, Temp("xray-dir-pb-"), Temp("xray-dir-sbx-")), new NameOverlayStore(Temp("xray-dir-names-")),
            new SequenceStore(Temp("xray-dir-seqs-")), experiments, new BehaviorStore(Temp("xray-dir-watch-")), directorRoot,
            new SafeMutationService(Temp("xray-dir-ops-"), Temp("xray-dir-bak-")));
        var window = new XRayWindow(vm);
        WpfHost.Show(window, width);
        Assert.True(WpfHost.PumpUntil(() => !vm.IsLoading), "The character index never finished loading.");
        return new Opened(vm, window, runner, experiment, Path.Combine(root, "chars", "ComboGuy"), directorRoot);
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

    /// <summary>Teaches from the experiment through the wizard and saves the draft with the Save button.</summary>
    private static DirectorLens TeachAndSave(Opened o)
    {
        var lens = o.Vm.Director;
        lens.TeachFromExperiment(o.Experiment);
        WpfHost.Layout(o.Window, o.Window.ActualWidth);
        Assert.Equal(XRayLensKind.Director, o.Vm.ActiveLens);
        Assert.True(lens.Editing);
        Assert.True(Named<FrameworkElement>(o.Window, "Teach AI wizard").IsVisible);
        lens.TrialsText = "1";
        Named<Button>(o.Window, "Save the draft").Command.Execute(null);
        WpfHost.Layout(o.Window, o.Window.ActualWidth);
        Assert.False(lens.Editing, lens.Status);
        return lens;
    }

    [Fact]
    public void TeachingFromAnExperimentPreFillsThePlainWizardAndSavingWritesOnlyADraft() => WpfHost.Run(() =>
    {
        var o = Open();
        try
        {
            var before = DirectorWorkspace.HashFolder(o.Installed);
            var lens = o.Vm.Director;
            lens.TeachFromExperiment(o.Experiment);
            WpfHost.Layout(o.Window, o.Window.ActualWidth);
            // The plain questions, answered from the run: attack distance 35 (where the chase stopped), as they get up (the wait), the proven follow-up.
            Assert.Equal(("35", FollowUpTiming.AsTheyGetUp), (lens.AttackDistanceText, lens.Timing!.Value));
            Assert.True(lens.FollowUp!.Choice.Proof.Proven);
            Assert.StartsWith("✓ ", lens.FollowUp.Text);
            Assert.StartsWith("Sequence Lab: ", lens.DraftSource);
            Assert.Equal(lens.Whens.Count, Named<ComboBox>(o.Window, "When").Items.Count);
            Assert.Equal("35", Named<TextBox>(o.Window, "Attack distance").Text);
            Assert.False(lens.ApproveCommand.CanExecute(null));

            lens.TrialsText = "1";
            Named<Button>(o.Window, "Save the draft").Command.Execute(null);
            WpfHost.Layout(o.Window, o.Window.ActualWidth);
            Assert.Equal(("Draft", "Owned"), (lens.CardStatus, lens.OwnershipKind));
            Assert.True(lens.ProofOk);
            Assert.Contains("35", Named<TextBlock>(o.Window, "Card do").Text);
            Assert.Contains("lying", Named<TextBlock>(o.Window, "Card when").Text);
            Assert.False(lens.HasProblems, lens.Problems);
            Assert.False(lens.ApproveCommand.CanExecute(null));                                                    // a draft cannot be approved…
            Assert.False(lens.DeployCommand.CanExecute(null));                                                     // …or deployed
            Assert.True(lens.TestCommand.CanExecute(null));
            Assert.Equal(before, DirectorWorkspace.HashFolder(o.Installed));                                       // nothing written to the install
            Assert.False(lens.Service!.Workspace.Exists);                                                          // nor a working copy yet

            // An unproven follow-up cannot be saved: the wizard says why.
            lens.TeachFromAbility(TeachAi.AbilityOf(o.Vm.Index!, 210)!);
            Assert.False(lens.FollowUp!.Choice.Proof.Proven);
            lens.FollowUp = lens.FollowUps.Single(f => f.Choice.EntryState == 210);
            lens.SaveCommand.Execute(null);
            Assert.StartsWith("Not saved: ", lens.Status);
            Assert.Contains("no runtime proof", lens.Status);
            Assert.Single(lens.Behaviors);
        }
        finally { o.Window.Close(); }
    });

    [Fact]
    public void TestingRunsEverySetupFromTheWorkingCopyAndWhyIsExactForTheGeneratedAttack() => WpfHost.Run(() =>
    {
        var o = Open();
        try
        {
            var before = DirectorWorkspace.HashFolder(o.Installed);
            var lens = TeachAndSave(o);
            Named<Button>(o.Window, "Test the taught behavior").Command.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => o.Vm.PlaybackSession.State == PlaybackState.Finished && lens.Runs.Count == 3, 30000), $"The test never finished: {lens.Status} {lens.Headline}");
            WpfHost.Layout(o.Window, o.Window.ActualWidth);

            Assert.Equal(["Proven setup (idle opponent)", "Opponent active after the knockdown", "Natural match (30 s)"], lens.Runs.Select(r => r.Metrics.Setup));
            Assert.StartsWith("Proven setup (idle opponent): 1 chase(s), 1 follow-up(s), 1 connected", lens.Runs[0].Text);
            Assert.Contains("1 fallback(s)", lens.Runs[1].Text);
            Assert.All(o.Runner.Generated.Take(3), g => Assert.Equal(CharacterFacts.GeneratedFile, g));            // the fixtures ran the generated build
            Assert.All(o.Runner.Sandboxes, s => Assert.False(Directory.Exists(s)));
            Assert.Equal(before, DirectorWorkspace.HashFolder(o.Installed));                                        // testing never touches the install
            Assert.Contains("+[State -1, IKEMEN Lab Director - Knockdown Chase: start]", lens.DiffText);
            // This mock engine cannot replay the proven sequence, so that regression fails — and the behavior honestly stays in Testing, unapprovable.
            Assert.Contains("Regression failed — The proven sequence still plays", lens.ReportFindings);
            Assert.Equal("Testing", lens.CardStatus);
            Assert.False(lens.ApproveCommand.CanExecute(null));

            // Why at the attack decision: exact, from the intention register, confirmed by what executed.
            Assert.Equal(lens.Runs[0], lens.WhyRun);
            var attack = lens.Moments.First(m => m.IsDecision && m.Text.Contains("attack", StringComparison.Ordinal));
            lens.WhyAt(attack.Frame + 3);
            WpfHost.Layout(o.Window, o.Window.ActualWidth);
            Assert.Equal("Exact", lens.WhyKind);
            Assert.Contains("chosen because the generated Knockdown Chase reached its attack-distance condition: 34 px ≤ 35 px", lens.WhyStatement);
            Assert.Contains("confirmed", lens.WhyVerification);
            Assert.Equal("Exact (intention register, confirmed)", Named<TextBlock>(o.Window, "Why badge").Text);
            // Before the hand-over (the opening the driver pressed) it claims nothing.
            lens.WhyAt(10);
            Assert.Equal(DirectorWhy.NotGenerated, lens.WhyStatement);
            Assert.NotEqual("Exact", lens.WhyKind);
        }
        finally { o.Window.Close(); }
    });

    [Fact]
    public void ApprovalIsItsOwnStepAndDeploymentGoesThroughSafeMutationAndRollsBack() => WpfHost.Run(() =>
    {
        var o = Open();
        var qa = UserDialogs.QaMode;
        UserDialogs.QaMode = true;
        try
        {
            var lens = TeachAndSave(o);
            var installedBefore = Directory.EnumerateFiles(o.Installed, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
            // A passing test of this exact build (the suite itself is covered above and in Core): generate, record, reload.
            var svc = lens.Service!;
            var b = lens.SelectedBehavior!.Behavior;
            var analysis = svc.Analyze(b, EngineIdentity.Sha256(o.Vm.Combos.Playback.CheckSetup(false)!.EnginePath), o.Vm.ExperimentStore, [o.Vm.PlaybackService.StoreRoot]);
            var (generated, hash) = svc.GenerateWorkingCopy(analysis);
            var report = new DirectorTestReport("t-ui", DateTime.UtcNow, b.Id, generated.Revision, generated.ModelHash, analysis.Code!.Hash, hash, null, [], [], [], false, true, [])
            {
                Directory = Path.Combine(svc.TestsRoot, "t-ui")
            };
            DirectorTesting.Save(report);
            svc.RecordTest(generated, report);
            lens.Build(o.Vm.Index!);
            WpfHost.Layout(o.Window, o.Window.ActualWidth);
            Assert.Equal("Ready for approval", lens.CardStatus);
            Assert.False(lens.DeployCommand.CanExecute(null));                                                      // ready is not approved

            // "No" leaves it unapproved; "Yes" approves this exact build — and deploys nothing.
            UserDialogs.QaConfirmAnswer = false;
            Named<Button>(o.Window, "Approve this exact build").Command.Execute(null);
            Assert.Null(lens.SelectedBehavior!.Behavior.Approval);
            UserDialogs.QaConfirmAnswer = true;
            Named<Button>(o.Window, "Approve this exact build").Command.Execute(null);
            WpfHost.Layout(o.Window, o.Window.ActualWidth);
            Assert.Equal(hash, lens.SelectedBehavior!.Behavior.Approval!.BuildHash);
            Assert.StartsWith("Approved build", lens.Status);
            Assert.All(installedBefore, kv => Assert.Equal(kv.Value, File.ReadAllBytes(kv.Key)));
            Assert.Contains(UserDialogs.RecordedWarnings, w => w.Contains("Approving does not deploy", StringComparison.Ordinal));

            // Deploy: the install becomes the approved build, through SafeMutation with a manifest; Roll back restores every byte.
            Named<Button>(o.Window, "Deploy the approved build").Command.Execute(null);
            WpfHost.Layout(o.Window, o.Window.ActualWidth);
            Assert.Equal("Live", lens.SelectedBehavior!.Behavior.Status.ToString());
            Assert.Equal(DirectorWorkspace.HashFolder(svc.Workspace.WorkingFolder), DirectorWorkspace.HashFolder(o.Installed));
            Assert.True(File.Exists(lens.SelectedBehavior.Behavior.Deployment!.ManifestPath));
            Assert.False(lens.TestCommand.CanExecute(null));                                                        // a live behavior is changed by rolling back first
            Named<Button>(o.Window, "Roll back the deployment").Command.Execute(null);
            Assert.True(lens.SelectedBehavior!.Behavior.Deployment!.RolledBack);
            var after = Directory.EnumerateFiles(o.Installed, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
            Assert.Equal(installedBefore.Keys.Order(), after.Keys.Order());
            Assert.All(installedBefore, kv => Assert.Equal(kv.Value, after[kv.Key]));
        }
        finally
        {
            UserDialogs.QaMode = qa;
            UserDialogs.QaConfirmAnswer = true;
            o.Window.Close();
        }
    });

    [Fact]
    public void ARecognisedChaseInWatchAndAskTeachesTheSameWizard() => WpfHost.Run(() =>
    {
        var o = Open();
        try
        {
            o.Vm.ActiveLens = XRayLensKind.Watch;
            WpfHost.Layout(o.Window, o.Window.ActualWidth);
            var watch = o.Vm.WatchAsk;
            Named<Button>(o.Window, "Watch a match").Command.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => o.Vm.PlaybackSession.State == PlaybackState.Finished && watch.Runs.Count == 1), "The watched match never finished.");
            watch.SelectedEpisode = watch.Episodes.First(e => e.Episode.Template == BehaviorTemplates.KnockdownChase);
            Assert.True(watch.TeachAiCommand.CanExecute(null));
            watch.TeachAiCommand.Execute(null);
            WpfHost.Layout(o.Window, o.Window.ActualWidth);
            Assert.Equal(XRayLensKind.Director, o.Vm.ActiveLens);
            Assert.True(o.Vm.Director.Editing, o.Vm.Director.Status);
            Assert.Contains("Watch", o.Vm.Director.DraftSource, StringComparison.OrdinalIgnoreCase);
            Assert.True(Named<FrameworkElement>(o.Window, "Teach AI wizard").IsVisible);
        }
        finally { o.Window.Close(); }
    });
}
