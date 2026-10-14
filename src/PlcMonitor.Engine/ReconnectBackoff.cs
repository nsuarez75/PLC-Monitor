using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Engine;

/// <summary>Espera exponencial entre intentos de conexión: inicial, inicial×m, inicial×m², ... hasta el máximo.</summary>
internal sealed class ReconnectBackoff(ReconnectSettings settings)
{
    private int _attempt;

    /// <summary>Intentos fallidos desde la última conexión correcta.</summary>
    public int Attempts => _attempt;

    public TimeSpan Next()
    {
        var ms = settings.InitialDelayMs * Math.Pow(settings.Multiplier, _attempt);
        if (_attempt < 64)
            _attempt++;
        return TimeSpan.FromMilliseconds(Math.Min(ms, settings.MaxDelayMs));
    }

    public void Reset() => _attempt = 0;
}
