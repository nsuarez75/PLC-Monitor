using Avalonia.Threading;

namespace PlcMonitor.App.Services;

/// <summary>Despachador sobre el hilo de la UI de Avalonia.</summary>
public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
