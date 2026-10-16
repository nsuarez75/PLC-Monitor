using System.Globalization;

namespace PlcMonitor.App.Charts;

/// <summary>Punto de una serie. <see cref="Value"/> null = sin valor (calidad mala): la línea se corta.</summary>
public readonly record struct ChartPoint(DateTimeOffset Time, double? Value);

/// <summary>
/// Serie de la gráfica, con sus puntos ordenados por tiempo. En escalón (<see cref="Step"/>) el valor se mantiene hasta
/// el siguiente punto y el último se prolonga hasta el final de la ventana: así se dibujan los datos por cambio (tags en
/// vivo e histórico en bruto). Sin escalón, se unen los puntos con rectas (agregados).
/// </summary>
public sealed class ChartSeries(string name, IReadOnlyList<ChartPoint> points, bool step = true)
{
    public string Name { get; } = name;

    public IReadOnlyList<ChartPoint> Points { get; } = points;

    public bool Step { get; } = step;

    /// <summary>Booleanos: el eje muestra 0/1 y los valores se leen como false/true.</summary>
    public bool IsBoolean { get; init; }

    /// <summary>Color de la paleta (App.Chart0…7): fijo por tag aunque se oculten otros.</summary>
    public int ColorIndex { get; init; }

    /// <summary>Valor de la serie en un instante (el último punto anterior o igual); null si no hay o es un hueco.</summary>
    public double? ValueAt(DateTimeOffset time)
    {
        var index = LastIndexAtOrBefore(time);
        if (index < 0)
            return null;
        if (Step || index == Points.Count - 1 || Points[index].Value is null || Points[index + 1].Value is null)
            return Points[index].Value;
        // Recta entre los dos puntos que rodean el instante.
        var (a, b) = (Points[index], Points[index + 1]);
        var span = (b.Time - a.Time).TotalMilliseconds;
        return span <= 0 ? a.Value : a.Value + ((b.Value - a.Value) * (time - a.Time).TotalMilliseconds / span);
    }

    /// <summary>Índice del último punto con Time ≤ <paramref name="time"/>, o -1 (búsqueda binaria).</summary>
    public int LastIndexAtOrBefore(DateTimeOffset time)
    {
        int lo = 0, hi = Points.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Points[mid].Time <= time)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    public string Format(double? value) => value is not { } v ? "—" : IsBoolean ? (v >= 0.5 ? "true" : "false") : ChartAxis.FormatValue(v);
}

/// <summary>Cálculo de los ejes: rango y marcas «redondas» en Y, y marcas de tiempo en X.</summary>
public static class ChartAxis
{
    private static readonly TimeSpan[] TimeSteps =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3),
        TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7),
    ];

    /// <summary>Rango del eje Y que cubre [min, max] con marcas en múltiplos de 1, 2 o 5 × 10ⁿ.</summary>
    public static (double Min, double Max, double Step) NiceRange(double min, double max, int targetTicks = 5)
    {
        if (double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max))
            return (0, 1, 0.2);
        if (max < min)
            (min, max) = (max, min);
        if (max - min < 1e-12)
        {
            // Valor constante: un margen alrededor para que la línea quede en medio.
            var pad = Math.Abs(min) < 1e-12 ? 1 : Math.Abs(min) * 0.1;
            (min, max) = (min - pad, max + pad);
        }
        var step = NiceStep((max - min) / Math.Max(1, targetTicks));
        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
    }

    /// <summary>Paso «redondo» (1, 2, 5 × 10ⁿ) más cercano por arriba a <paramref name="raw"/>.</summary>
    public static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw))
            return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var fraction = raw / magnitude;
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    /// <summary>Paso de tiempo de la lista que deja como mucho <paramref name="maxTicks"/> marcas en la ventana.</summary>
    public static TimeSpan TimeStep(TimeSpan span, int maxTicks)
    {
        maxTicks = Math.Max(1, maxTicks);
        foreach (var step in TimeSteps)
        {
            if (span.Ticks / step.Ticks <= maxTicks)
                return step;
        }
        return TimeSpan.FromDays(Math.Ceiling(span.TotalDays / maxTicks));
    }

    /// <summary>Marcas de tiempo alineadas al paso (en hora local, para que caigan en minutos y horas «redondos»).</summary>
    public static IEnumerable<DateTimeOffset> TimeTicks(DateTimeOffset from, DateTimeOffset to, TimeSpan step)
    {
        if (step <= TimeSpan.Zero || to <= from)
            yield break;
        var local = from.ToLocalTime();
        var offset = local.Offset;
        long first;
        if (step >= TimeSpan.FromDays(1))
            first = local.Date.Ticks;
        else
            first = local.Ticks / step.Ticks * step.Ticks;
        for (var t = new DateTimeOffset(first, offset); t <= to; t += step)
        {
            if (t >= from)
                yield return t;
        }
    }

    /// <summary>Texto de una marca de tiempo según el paso y la ventana.</summary>
    public static string FormatTime(DateTimeOffset time, TimeSpan step, TimeSpan span)
    {
        var local = time.ToLocalTime();
        var format = step >= TimeSpan.FromDays(1) ? "dd/MM"
            : span > TimeSpan.FromDays(1) ? "dd/MM HH:mm"
            : step < TimeSpan.FromMinutes(1) ? "HH:mm:ss"
            : "HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    public static string FormatValue(double value)
    {
        if (value == 0)
            value = 0; // sin «-0»
        var abs = Math.Abs(value);
        return abs != 0 && (abs >= 1e7 || abs < 1e-3)
            ? value.ToString("0.###E+0", CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
