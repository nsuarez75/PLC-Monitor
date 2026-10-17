namespace PlcMonitor.App.Charts;

/// <summary>Punto con X numérica; <see cref="Y"/> null = hueco (la línea se corta y X no cuenta).</summary>
public readonly record struct XYPoint(double X, double? Y);

/// <summary>
/// Curva de una gráfica con eje X numérico (ciclos superpuestos y X-Y). Las curvas de un mismo tag comparten color; la
/// resaltada (<see cref="Highlight"/>, el último ciclo) se dibuja opaca y encima, las demás tenues.
/// </summary>
/// <param name="IsOut">Se sale de la banda de tolerancia de su referencia: se dibuja en rojo.</param>
public sealed record XYCurve(string Name, IReadOnlyList<XYPoint> Points, int ColorIndex, bool Highlight = true, bool Step = false, bool IsBoolean = false,
    bool IsOut = false);

/// <summary>Activación del disparo: cuándo empezó a cumplirse y cuándo dejó de hacerlo (null = sigue).</summary>
public readonly record struct TriggerActivation(DateTimeOffset On, DateTimeOffset? Off);

/// <summary>Cálculo de las curvas de ciclos superpuestos y de X-Y a partir de las series en el tiempo.</summary>
public static class XYData
{
    /// <summary>Cada vez que el disparo empieza a cumplirse, hasta que deja de hacerlo (sin unir activaciones cercanas).</summary>
    public static List<TriggerActivation> Activations(IReadOnlyList<ChartPoint> trigger, Func<double?, bool> met)
    {
        var activations = new List<TriggerActivation>();
        DateTimeOffset? on = null;
        foreach (var point in trigger)
        {
            var isMet = met(point.Value);
            if (isMet && on is null)
            {
                on = point.Time;
            }
            else if (!isMet && on is { } start)
            {
                activations.Add(new TriggerActivation(start, point.Time));
                on = null;
            }
        }
        if (on is { } open)
            activations.Add(new TriggerActivation(open, null));
        return activations;
    }

    /// <summary>Tramo de tiempo de un ciclo: desde <paramref name="before"/> antes de la activación hasta <paramref name="hold"/> después de acabar (como mucho, <paramref name="now"/>).</summary>
    public static (DateTimeOffset From, DateTimeOffset To) CycleWindow(TriggerActivation activation, TimeSpan before, TimeSpan hold, DateTimeOffset now)
    {
        var to = (activation.Off ?? now) + hold;
        return (activation.On - before, to < now ? to : now);
    }

    /// <summary>
    /// Ciclo de una serie: sus valores en [from, to] con X = segundos desde <paramref name="zero"/> (la activación). Empieza
    /// con el valor que tenía en <paramref name="from"/> y, en escalón, mantiene el último hasta <paramref name="to"/>.
    /// </summary>
    public static List<XYPoint> Cycle(ChartSeries series, DateTimeOffset zero, DateTimeOffset from, DateTimeOffset to)
    {
        var points = new List<XYPoint>();
        if (to < from)
            return points;
        double X(DateTimeOffset t) => (t - zero).TotalSeconds;
        var first = series.LastIndexAtOrBefore(from);
        if (first >= 0)
            points.Add(new XYPoint(X(from), series.Points[first].Value));
        for (var i = Math.Max(first + 1, 0); i < series.Points.Count && series.Points[i].Time <= to; i++)
            points.Add(new XYPoint(X(series.Points[i].Time), series.Points[i].Value));
        if (series.Step && points.Count > 0 && points[^1].Y is { } last && X(to) > points[^1].X)
            points.Add(new XYPoint(X(to), last));
        return points;
    }

    /// <summary>
    /// Pares (x, y) de dos series en [from, to]: en cada instante en que cambia alguna, el valor de cada una en ese
    /// momento. Si falta alguno de los dos, hueco.
    /// </summary>
    public static List<XYPoint> Pairs(ChartSeries x, ChartSeries y, DateTimeOffset from, DateTimeOffset to)
    {
        var times = new SortedSet<DateTimeOffset> { from };
        foreach (var series in new[] { x, y })
        {
            for (var i = Math.Max(0, series.LastIndexAtOrBefore(from) + 1); i < series.Points.Count && series.Points[i].Time <= to; i++)
                times.Add(series.Points[i].Time);
        }
        var pairs = new List<XYPoint>(times.Count);
        foreach (var t in times)
        {
            var (vx, vy) = (x.ValueAt(t), y.ValueAt(t));
            if (vx is { } px && vy is not null)
                pairs.Add(new XYPoint(px, vy));
            else if (pairs.Count > 0 && pairs[^1].Y is not null)
                pairs.Add(new XYPoint(0, null));
        }
        return pairs;
    }
}
