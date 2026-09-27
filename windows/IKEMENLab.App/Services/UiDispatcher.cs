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

    public Task InvokeAsync(Action action)
        => _dispatcher.InvokeAsync(action).Task;
}
