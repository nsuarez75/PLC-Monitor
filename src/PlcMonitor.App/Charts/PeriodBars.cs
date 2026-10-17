using System.Globalization;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.Charts;

/// <summary>
/// Barras por periodo (hora, turno de 8 h o día, en hora local) a partir del histórico: el agente agrega por intervalos
/// del tamaño del periodo contados desde el inicio de la consulta, así que la consulta empieza en el inicio de un periodo.
/// </summary>
public static class PeriodBars
{
    private static readonly string[] WeekDays = ["dom", "lun", "mar", "mié", "jue", "vie", "sáb"];

    public static TimeSpan Length(BarPeriod period) => period switch
    {
        BarPeriod.Shift => TimeSpan.FromHours(8),
        BarPeriod.Day => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(1),
    };

    /// <summary>Inicio (hora local) del periodo que contiene <paramref name="time"/>.</summary>
    public static DateTimeOffset Start(DateTimeOffset time, BarPeriod period, int shiftStartHour)
    {
        var local = time.ToLocalTime();
        var midnight = new DateTimeOffset(local.Date, local.Offset);
        switch (period)
        {
            case BarPeriod.Day:
                return midnight;
            case BarPeriod.Shift:
                var first = midnight.AddHours(Math.Clamp(shiftStartHour, 0, 23));
                if (local < first)
                    first = first.AddDays(-1);
                return first.AddHours(Math.Floor((local - first).TotalHours / 8) * 8);
            default:
                return midnight.AddHours(local.Hour);
        }
    }

    /// <summary>Texto de un periodo: «17/10 14:00», «17/10 T2» (turno 1 = el que empieza a la hora de inicio), «vie 17/10».</summary>
    public static string Label(DateTimeOffset start, BarPeriod period, int shiftStartHour)
    {
        // Días a mano: la app corre con globalización invariante (sin culturas).
        var local = start.ToLocalTime();
        var culture = CultureInfo.InvariantCulture;
        return period switch
        {
            BarPeriod.Day => $"{WeekDays[(int)local.DayOfWeek]} {local.ToString("dd/MM", culture)}",
            BarPeriod.Shift => $"{local.AddHours(-Math.Clamp(shiftStartHour, 0, 23)).ToString("dd/MM", culture)} T{(((local.Hour - shiftStartHour + 24) % 24) / 8) + 1}",
            _ => local.ToString("dd/MM HH:mm", culture),
        };
    }

    /// <summary>Agregado que se pide al agente para cada cálculo (el incremento sale de los últimos de cada periodo).</summary>
    public static HistoryAggregate Aggregate(BarMetric metric) => metric switch
    {
        BarMetric.Max => HistoryAggregate.Max,
        BarMetric.Min => HistoryAggregate.Min,
        BarMetric.Increase => HistoryAggregate.Last,
        _ => HistoryAggregate.Average,
    };

    /// <summary>
    /// Barras calculadas aquí a partir de los puntos (CSV o histórico ya leído): un punto por periodo con valores, con su
    /// inicio como hora. La media es la de las muestras; el incremento suma lo que sube entre muestras seguidas (si baja,
    /// el contador se ha reiniciado y cuenta su valor) y se apunta al periodo de la muestra nueva.
    /// </summary>
    public static List<ChartPoint> Compute(IReadOnlyList<ChartPoint> points, BarPeriod period, BarMetric metric, int shiftStartHour)
    {
        var groups = new SortedDictionary<DateTimeOffset, List<double>>();
        double? previous = null;
        foreach (var point in points)
        {
            if (point.Value is not { } value)
                continue;
            var start = Start(point.Time, period, shiftStartHour);
            if (!groups.TryGetValue(start, out var values))
                groups[start] = values = [];
            if (metric != BarMetric.Increase)
                values.Add(value);
            else if (previous is { } p)
                values.Add(value >= p ? value - p : value);
            else
                values.Add(0);
            previous = value;
        }
        return groups.Select(g => new ChartPoint(g.Key, metric switch
        {
            BarMetric.Max => g.Value.Max(),
            BarMetric.Min => g.Value.Min(),
            BarMetric.Increase => g.Value.Sum(),
            _ => g.Value.Average(),
        })).ToList();
    }

    /// <summary>
    /// Incremento de un contador por periodo a partir del último valor de cada uno (el primero solo sirve de referencia):
    /// la diferencia con el anterior o, si el contador se ha reiniciado (baja), su valor actual.
    /// </summary>
    public static List<ChartPoint> Increase(IReadOnlyList<ChartPoint> lasts)
    {
        var result = new List<ChartPoint>();
        double? previous = null;
        foreach (var point in lasts)
        {
            if (point.Value is not { } value)
                continue;
            if (previous is { } p)
                result.Add(new ChartPoint(point.Time, value >= p ? value - p : value));
            previous = value;
        }
        return result;
    }
}
