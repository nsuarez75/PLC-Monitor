using System.Globalization;
using PlcMonitor.Abstractions;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Historian;

/// <summary>
/// Decide qué valores de un tag se guardan (estilo Ignition):
/// <list type="bullet">
/// <item>el primer valor y todo cambio de calidad, siempre;</item>
/// <item>los cambios numéricos dentro de la banda muerta, no;</item>
/// <item>si llegan más rápido que la frecuencia mínima, se guarda el último al cumplirse el intervalo (no se pierde);</item>
/// <item>si pasa el tiempo máximo sin guardar, se repite el valor actual.</item>
/// </list>
/// </summary>
internal sealed class HistorySampler(TagKey key, TagHistorySettings settings)
{
    private TagValue? _lastStored;
    private DateTimeOffset _lastStoredAt;
    private TagValue? _pending;
    private TagValue? _current;

    public TagKey Key { get; } = key;

    public TagHistorySettings Settings { get; } = settings;

    /// <summary>Nuevo valor del tag. Devuelve la muestra a guardar, o null.</summary>
    public HistorySample? Offer(TagValue value, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(value);
        _current = value;
        if (_lastStored is null || value.Quality != _lastStored.Quality)
            return Store(value, value.Timestamp, now);
        if (!PassesDeadband(value, _lastStored))
            return null;
        if (now - _lastStoredAt < TimeSpan.FromMilliseconds(Settings.MinIntervalMs))
        {
            _pending = value;
            return null;
        }
        return Store(value, value.Timestamp, now);
    }

    /// <summary>Paso del reloj: guarda el valor pendiente al cumplirse la frecuencia mínima o repite el actual al cumplirse el tiempo máximo.</summary>
    public HistorySample? Tick(DateTimeOffset now)
    {
        if (_pending is { } pending && now - _lastStoredAt >= TimeSpan.FromMilliseconds(Settings.MinIntervalMs))
            return Store(pending, pending.Timestamp, now);
        if (Settings.MaxIntervalMs > 0 && _current is { } current && now - _lastStoredAt >= TimeSpan.FromMilliseconds(Settings.MaxIntervalMs))
            return Store(current, now, now);
        return null;
    }

    private HistorySample Store(TagValue value, DateTimeOffset timestamp, DateTimeOffset now)
    {
        _lastStored = value;
        _lastStoredAt = now;
        _pending = null;
        return new HistorySample(Key, timestamp, value.Value, value.DataType, value.Quality);
    }

    private bool PassesDeadband(TagValue value, TagValue last)
    {
        if (Settings.Deadband <= 0 || ToNumber(value.Value) is not { } v || ToNumber(last.Value) is not { } previous)
            return true;
        var limit = Settings.DeadbandMode == DeadbandMode.Percent ? Math.Abs(previous) * Settings.Deadband / 100 : Settings.Deadband;
        return Math.Abs(v - previous) > limit;
    }

    /// <summary>Valor numérico de una muestra (bool como 0/1), o null si no es numérica.</summary>
    public static double? ToNumber(object? value) => value switch
    {
        bool b => b ? 1 : 0,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => null,
    };
}
