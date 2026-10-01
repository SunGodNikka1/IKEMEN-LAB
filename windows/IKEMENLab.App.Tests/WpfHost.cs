using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace IKEMENLab.App.Tests;

/// <summary>
/// One long-lived STA thread with one dispatcher and one <see cref="Application"/> for the whole test run. Tests marshal their bodies onto it,
/// so there is exactly one Application (no creation race), controls always live on the thread that made them, and windows are really shown and
/// laid out before anything is asserted.
///
/// Pattern for a window test: construct on the host thread → <see cref="Show"/> → the dispatcher runs Loaded/Render/idle work → measure and
/// assert → <see cref="CloseAll"/> in a finally (done by <see cref="Run"/> for every window it was told about).
/// </summary>
internal static class WpfHost
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static bool _appCreated;

    private static Dispatcher Dispatcher
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher is not null) return _dispatcher;
                using var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                { IsBackground = true, Name = "WPF test host" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait();
                return _dispatcher!;
            }
        }
    }

    private static readonly System.Collections.Generic.List<Window> Opened = new();

    /// <summary>Runs <paramref name="body"/> on the host thread, then closes every window it showed, whether or not it threw. Exceptions propagate to the test.</summary>
    public static void Run(Action body)
    {
        Dispatcher.Invoke(() =>
        {
            EnsureAppResources();
            try { body(); }
            finally { CloseAll(); }
        }, DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromMinutes(2));
    }

    /// <summary>Views resolve styles and converters from App.xaml; the test host has no App, so load the same dictionaries once.</summary>
    private static void EnsureAppResources()
    {
        lock (Gate)
        {
            if (_appCreated || Application.Current is not null) { _appCreated = true; return; }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var uri in new[]
            {
                "pack://application:,,,/IKEMENLab;component/Themes/Colors.Dark.xaml",
                "pack://application:,,,/IKEMENLab;component/Themes/Controls.xaml",
            })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uri) });
            _appCreated = true;
        }
    }

    /// <summary>Really shows the window (not activated, not in the taskbar) at the given width and lets Loaded, binding and layout work run before returning.</summary>
    public static void Show(Window window, double width, double height = 900)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 20;
        window.Top = 20;
        window.Width = width;
        window.Height = height;
        Opened.Add(window);
        window.Show();
        Layout(window, width, height);
    }

    /// <summary>Lets queued dispatcher work (Loaded, data binding, render) run, then forces a layout pass.</summary>
    public static void Layout(Window window, double width, double height = 900)
    {
        Pump(DispatcherPriority.Loaded);
        Pump(DispatcherPriority.Render);
        Pump(DispatcherPriority.ApplicationIdle);
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, window.ActualWidth > 0 ? window.ActualWidth : width, window.ActualHeight > 0 ? window.ActualHeight : height));
        window.UpdateLayout();
    }

    /// <summary>Runs everything queued at or above <paramref name="priority"/> and returns.</summary>
    public static void Pump(DispatcherPriority priority = DispatcherPriority.Background)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds. Returns false on timeout (the caller asserts, so the failure names the condition).</summary>
    public static bool PumpUntil(Func<bool> condition, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) return false;
            Pump(DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        return true;
    }

    /// <summary>Closes every window the test showed. Safe to call twice.</summary>
    public static void CloseAll()
    {
        foreach (var w in Opened.ToArray())
        {
            try { if (w.IsLoaded || w.IsVisible) w.Close(); }
            catch (InvalidOperationException) { /* already closing */ }
        }

        Opened.Clear();
        Pump(DispatcherPriority.Background);
    }
}
