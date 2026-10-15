namespace PlcMonitor.App.Services;

/// <summary>Ejecuta código en el hilo de la interfaz (en los tests, al momento).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Despachador inmediato: para tests y para código que ya está en el hilo de la UI.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
