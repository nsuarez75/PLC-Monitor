using System.Globalization;

namespace PlcMonitor.App.Charts;

/// <summary>Tramo en que un tag mantiene el mismo valor; <see cref="Value"/> null = sin valor (calidad mala).</summary>
public readonly record struct StateSegment(DateTimeOffset From, DateTimeOffset To, double? Value)
{
    public TimeSpan Duration => To - From;
}

/// <summary>Tiempo total en un estado y su parte del tiempo con valor.</summary>
public readonly record struct StateShare(double Value, TimeSpan Duration, double Fraction);

/// <summary>Cronograma de estados: tramos de valor constante de una serie en una ventana, y el reparto del tiempo.</summary>
public static class StateTimeline
{
    /// <summary>Tramos de la serie dentro de [from, to] (cada valor se mantiene hasta el siguiente punto; iguales seguidos, unidos).</summary>
    public static List<StateSegment> Segments(ChartSeries series, DateTimeOffset from, DateTimeOffset to)
    {
        var segments = new List<StateSegment>();
        if (to <= from)
            return segments;
        var points = series.Points;
        var first = series.LastIndexAtOrBefore(from);
        var start = from;
        double? value = first >= 0 ? points[first].Value : null;
        var hasStart = first >= 0;
        for (var i = Math.Max(first + 1, 0); i < points.Count && points[i].Time < to; i++)
        {
            var point = points[i];
            if (!hasStart)
            {
                (start, value, hasStart) = (point.Time, point.Value, true);
                continue;
            }
            if (point.Value == value)
                continue;
            if (point.Time > start)
                segments.Add(new StateSegment(start, point.Time, value));
            (start, value) = (point.Time, point.Value);
        }
        if (hasStart && to > start)
            segments.Add(new StateSegment(start, to, value));
        return segments;
    }

    /// <summary>Tiempo en cada estado (sin los huecos), de más a menos.</summary>
    public static List<StateShare> Shares(IEnumerable<StateSegment> segments)
    {
        var totals = new Dictionary<double, TimeSpan>();
        foreach (var segment in segments)
        {
            if (segment.Value is not { } v)
                continue;
            totals[v] = totals.GetValueOrDefault(v) + segment.Duration;
        }
        var all = totals.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        return totals.Select(t => new StateShare(t.Key, t.Value, all > TimeSpan.Zero ? t.Value / all : 0))
            .OrderByDescending(s => s.Duration).ThenBy(s => s.Value).ToList();
    }

    /// <summary>Duración legible: 850 ms, 12.3 s, 4 min 05 s, 2 h 07 min.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var culture = CultureInfo.InvariantCulture;
        if (duration < TimeSpan.FromSeconds(1))
            return $"{duration.TotalMilliseconds.ToString("0", culture)} ms";
        if (duration < TimeSpan.FromMinutes(1))
            return $"{duration.TotalSeconds.ToString("0.#", culture)} s";
        if (duration < TimeSpan.FromHours(1))
            return $"{(int)duration.TotalMinutes} min {duration.Seconds:00} s";
        return $"{(int)duration.TotalHours} h {duration.Minutes:00} min";
    }
}
