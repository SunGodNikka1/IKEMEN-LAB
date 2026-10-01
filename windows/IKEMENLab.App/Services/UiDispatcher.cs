using System.Windows;
using System.Windows.Threading;

namespace IKEMENLab.App.Services;

public sealed class UiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public UiDispatcher(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void Invoke(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.Invoke(action);
    }

    /// <summary>Queues the action on the UI thread and returns at once. Use this (not <see cref="Invoke"/>) from worker threads that the UI thread may be waiting on.</summary>
    public void Post(Action action) => _dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Action action)
        => _dispatcher.InvokeAsync(action).Task;
}
