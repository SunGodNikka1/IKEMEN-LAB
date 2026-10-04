using System;
using System.IO;
using System.Linq;
using IKEMENLab.App.ViewModels;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Names;
using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>Renaming in the real X-Ray window: one resolver feeds the details panel, Ability Atlas, State Graph and Combos lens; stale names wait for review.</summary>
[Collection(WpfUiCollection.Name)]
public class XRayNamesUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xray-names-ui-" + Guid.NewGuid().ToString("N"));
    private readonly string _store = Path.Combine(Path.GetTempPath(), "xray-names-store-" + Guid.NewGuid().ToString("N"));

    private const string Cmd = """
        [Command]
        name = "x"
        command = x
        [Command]
        name = "y"
        command = y

        [Statedef -1]

        [State -1, x]
        type = ChangeState
        value = 200
        triggerall = command = "x"
        trigger1 = statetype = S && ctrl

        [State -1, y on hit]
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

        [State 210, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0
        """;

    public XRayNamesUiTests()
    {
        void W(string rel, string text)
        {
            var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W("chars/hero/hero.def", "[Info]\nname = Hero\n[Files]\ncmd = hero.cmd\ncns = hero.cns\n");
        W("chars/hero/hero.cmd", Cmd);
        W("chars/hero/hero.cns", Cns);
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _store }) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; set; } = new();
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    private XRayViewModel Open(out XRayWindow window)
    {
        var entry = new CharacterEntry { Id = "hero", DisplayName = "Hero", Name = "Hero", Author = "", VersionDate = "", DefPath = "chars/hero/hero.def", FolderPath = "chars/hero" };
        var vm = new XRayViewModel(_root, entry, "Hero", new MemorySettings(), names: new NameOverlayStore(_store));
        window = new XRayWindow(vm);
        WpfHost.Show(window, 1200);
        Assert.True(WpfHost.PumpUntil(() => !vm.IsLoading), "The character index never finished loading.");
        return vm;
    }

    [Fact]
    public void ARenameShowsEverywhereAndKeepsTheIdVisible() => WpfHost.Run(() =>
    {
        var vm = Open(out var window);
        try
        {
            vm.Combos.FindRoutesCommand.Execute(null);
            Assert.True(WpfHost.PumpUntil(() => vm.Combos.Routes.Count > 0));
            var graphBefore = vm.Combos.Graph;
            var routeKeys = vm.Combos.Routes.Select(r => r.Id).ToList();
            var selectedKey = vm.Combos.SelectedRoute?.Id;

            vm.Select("state:210");
            Assert.True(vm.CanRenameSelected);
            vm.RenameText = "Revolver Shot";
            vm.RenameCommand.Execute(null);
            WpfHost.Pump();

            // A rename redraws labels only: same candidate graph, same routes, same selected route.
            Assert.Same(graphBefore, vm.Combos.Graph);
            Assert.Equal(routeKeys, vm.Combos.Routes.Select(r => r.Id).ToList());
            Assert.Equal(selectedKey, vm.Combos.SelectedRoute?.Id);

            Assert.Equal("Revolver Shot", vm.Title);
            Assert.Contains("state:210", vm.IdText);
            Assert.Contains("State 210", vm.IdText);
            Assert.Equal("Your name", vm.NameInfo);
            Assert.Contains(vm.Graph.Nodes, n => n.Id == "state:210" && n.Title == "Revolver Shot");
            Assert.Contains(vm.Atlas.View.Cast<XRayRow>(), r => r.Id == "ability:210" && r.Title == "Revolver Shot");

            Assert.Contains(vm.Combos.Routes, r => r.Title.Contains("Revolver Shot", StringComparison.Ordinal));

            vm.ResetNameCommand.Execute(null);
            WpfHost.Pump();
            Assert.Equal("State 210", vm.Title);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AChangedStateWaitsForReviewInsteadOfKeepingItsName() => WpfHost.Run(() =>
    {
        var vm = Open(out var window);
        vm.Select("state:200");
        vm.RenameText = "Normal A";
        vm.RenameCommand.Execute(null);
        window.Close();

        var cns = Path.Combine(_root, "chars", "hero", "hero.cns");
        File.WriteAllText(cns, File.ReadAllText(cns).Replace("anim = 200", "anim = 210"));

        var again = Open(out var window2);
        try
        {
            again.Select("state:200");
            Assert.Equal("State 200", again.Title);
            var row = Assert.Single(again.NameReview);
            Assert.Equal("Normal A", row.Label);
            Assert.True(row.CanKeep);

            again.KeepNameCommand.Execute(row);
            WpfHost.Pump();
            Assert.Equal("Normal A", again.Title);
            Assert.False(again.HasNameReview);
        }
        finally { window2.Close(); }
    });
}
