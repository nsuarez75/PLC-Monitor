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
/// <item>con disparo, solo mientras está activo: al activarse guarda el valor actual y, cuando la condición deja de
/// cumplirse, sigue el tiempo configurado y guarda un último punto al pararse.</item>
/// </list>
/// </summary>
internal sealed class HistorySampler(TagKey key, TagHistorySettings settings)
{
    private TagValue? _lastStored;
    private DateTimeOffset _lastStoredAt;
    private TagValue? _pending;
    private TagValue? _current;
    private bool _active = settings.Trigger is null;
    private DateTimeOffset? _holdUntil;

    public TagKey Key { get; } = key;

    public TagHistorySettings Settings { get; } = settings;

    /// <summary>Tag que dispara el histórico de este (mismo dispositivo); null si se historiza siempre.</summary>
    public TagKey? TriggerKey { get; } = settings.Trigger is { } t ? new TagKey(key.Device, t.Tag) : null;

    /// <summary>Se está guardando (sin disparo, siempre).</summary>
    public bool Active => _active;

    /// <summary>
    /// Nuevo valor del tag disparo. Al cumplirse la condición se activa y guarda el valor actual; al dejar de cumplirse
    /// sigue el tiempo configurado (lo cierra <see cref="Tick"/>).
    /// </summary>
    public HistorySample? OfferTrigger(TagValue trigger, DateTimeOffset now)
    {
        if (Settings.Trigger is not { } settings)
            return null;
        var met = trigger.Quality == QualityCode.Good && settings.IsMet(trigger.Value);
        if (met)
        {
            _holdUntil = null;
            if (_active)
                return null;
            _active = true;
            return _current is { } current ? Store(current, now, now) : null;
        }
        if (!_active || _holdUntil is not null)
            return null;
        _holdUntil = now + TimeSpan.FromMilliseconds(settings.HoldMs);
        return settings.HoldMs <= 0 ? Deactivate(now) : null;
    }

    private HistorySample? Deactivate(DateTimeOffset now)
    {
        _active = false;
        _holdUntil = null;
        // Último punto al pararse: el valor del final del tramo (sin él, la gráfica acabaría en el último cambio).
        var last = _current is { } current ? Store(current, now, now) : null;
        _lastStored = null;
        return last;
    }

    /// <summary>Nuevo valor del tag. Devuelve la muestra a guardar, o null.</summary>
    public HistorySample? Offer(TagValue value, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(value);
        _current = value;
        if (!_active)
            return null;
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
        if (_holdUntil is { } until && now >= until)
            return Deactivate(now);
        if (!_active)
            return null;
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
        // Un REAL (float) se pasa por su texto más corto para no arrastrar decimales espurios (516.32196, no 516.321960449219).
        float f => double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => null,
    };
}
