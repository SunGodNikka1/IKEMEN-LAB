using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IKEMENLab.App.Views;
using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>
/// Proves the real character-detail UI exposes an invokable X-Ray action.
/// Presence of XRayWindow in the assembly is not evidence of a user-facing entry point: the command can be
/// missing, unbound, clipped out of the panel, or permanently disabled and the type still resolves.
/// </summary>
public class CharacterDetailUiTests
{
    /// <summary>Minimal stand-in for the inspector's data context. WPF bindings to absent members simply fail silently.</summary>
    private sealed class StubContext
    {
        public bool CanShowTools { get; set; } = true;
        public ICommand? OpenSpritesCommand { get; set; }
        public ICommand? OpenTuningCommand { get; set; }
        public ICommand? OpenXRayCommand { get; set; }
        public object? Row { get; set; }
    }

    private sealed class Relay : ICommand
    {
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) { Executed = true; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool Executed { get; private set; }
    }

    /// <summary>Runs a WPF interaction on a dedicated STA thread with its own dispatcher.</summary>
    private static void OnSta(Action<Dispatcher> body)
    {
        Exception? failure = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(() =>
            {
                try { EnsureAppResources(); body(dispatcher); }
                catch (Exception ex) { failure = ex; }
                finally { ready.Set(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("WPF test body timed out.");
        if (failure is not null) throw failure;
    }

    /// <summary>Views resolve converters and styles from App.xaml; a bare test host has no Application, so load the same dictionaries.</summary>
    private static void EnsureAppResources()
    {
        if (System.Windows.Application.Current is not null) return;
        var app = new System.Windows.Application();
        foreach (var uri in new[]
        {
            "pack://application:,,,/IKEMENLab;component/Themes/Colors.Dark.xaml",
            "pack://application:,,,/IKEMENLab;component/Themes/Controls.xaml",
        })
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uri) });
        }
    }
    /// <summary>A detached UserControl has no visual tree until it is measured; force one before inspecting it.</summary>
    private static void EnsureLaidOut(FrameworkElement fe)
    {
        if (fe.ActualWidth > 0) return;
        fe.Measure(new Size(900, 2000));
        fe.Arrange(new Rect(0, 0, 900, 2000));
        fe.UpdateLayout();
    }
    private static Button Tool(DependencyObject root, string automationName)
    {
        EnsureLaidOut((FrameworkElement)root);
        return VisualTreeHelpers.Descendants(root).OfType<Button>()
            .SingleOrDefault(b => AutomationProperties.GetName(b) == automationName)
        ?? throw new InvalidOperationException(
            $"The character detail panel renders no button named '{automationName}'. " +
            "A missing X-Ray entry point must fail here, not in a screenshot.");
    }

    private static Button[] ToolRow(DependencyObject root)
    {
        EnsureLaidOut((FrameworkElement)root);
        return VisualTreeHelpers.Descendants(root).OfType<Button>()
            .Where(b => AutomationProperties.GetName(b) is "Inspect Sprites" or "Edit Size and Stats" or "Open Character X-Ray")
            .ToArray();
    }

    [Fact]
    public void SelectedCharacterPanelExposesInvokableXRayCommand()
    {
        OnSta(_ =>
        {
            var view = new CharacterInspectorView();
            var xray = new Relay();
            view.DataContext = new StubContext
            {
                CanShowTools = true,
                OpenSpritesCommand = new Relay(),
                OpenTuningCommand = new Relay(),
                OpenXRayCommand = xray,
                Row = new object(),
            };

            var button = Tool(view, "Open Character X-Ray");
            Assert.NotNull(button.Command);
            Assert.True(button.Command.CanExecute(button.CommandParameter), "The X-Ray action must be enabled for a valid character.");

            button.Command.Execute(button.CommandParameter);
            Assert.True(xray.Executed, "The X-Ray action must reach the existing command, not a dead binding.");
        });
    }

    [Fact]
    public void ExistingSpriteAndTuningActionsAreUnaffected()
    {
        OnSta(_ =>
        {
            var view = new CharacterInspectorView();
            var sprites = new Relay();
            var tuning = new Relay();
            view.DataContext = new StubContext
            {
                CanShowTools = true,
                OpenSpritesCommand = sprites,
                OpenTuningCommand = tuning,
                OpenXRayCommand = new Relay(),
                Row = new object(),
            };

            var s = Tool(view, "Inspect Sprites");
            s.Command!.Execute(s.CommandParameter);
            Assert.True(sprites.Executed);

            var t = Tool(view, "Edit Size and Stats");
            t.Command!.Execute(t.CommandParameter);
            Assert.True(tuning.Executed);
        });
    }

    /// <summary>
    /// Regression guard for the defect that hid X-Ray: the action row was one unclipped horizontal stack whose
    /// last child (X-Ray) was pushed past the panel edge and clipped, so the entry point existed but was
    /// unreachable. Every action must keep a real size in a panel far narrower than the single-row width.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(359)]
    [InlineData(900)]
    public void ToolActionsStayReachableAtNarrowPanelWidths(double width)
    {
        OnSta(_ =>
        {
            var view = new CharacterInspectorView();
            view.DataContext = new StubContext
            {
                CanShowTools = true,
                OpenSpritesCommand = new Relay(),
                OpenTuningCommand = new Relay(),
                OpenXRayCommand = new Relay(),
                Row = new object(),
            };

            view.Width = width;
            view.Measure(new Size(width, 2000));
            view.Arrange(new Rect(0, 0, width, 2000));
            view.UpdateLayout();

            foreach (var button in ToolRow(view))
            {
                var name = AutomationProperties.GetName(button);
                Assert.True(button.ActualWidth > 0, $"'{name}' collapsed to zero width at panel width {width}.");
                Assert.True(button.ActualHeight > 0, $"'{name}' has no height at panel width {width}.");
            }
        });
    }
}
internal static class VisualTreeHelpers
{
    public static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}




