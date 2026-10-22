using System.Globalization;
using System.Text;

namespace PlcMonitor.App.Charts;

/// <summary>
/// Estadísticas de una serie en una ventana. Las medias y la desviación están ponderadas en el tiempo (cada valor pesa lo
/// que dura, que es como se guarda el histórico, por cambio). Mín y máx llevan la hora en que se dan (la del inicio de la
/// ventana si es el valor que ya traía). En los booleanos cuentan <see cref="TruePercent"/> y <see cref="RisingEdges"/>.
/// </summary>
public sealed record SeriesStatsRow(string Name, int ColorIndex, bool IsBoolean, SeriesFormat Display)
{
    /// <summary>Muestras buenas dentro de la ventana (sin contar el valor que viene de antes).</summary>
    public int Samples { get; init; }

    public double? Min { get; init; }

    public DateTimeOffset? MinTime { get; init; }

    public double? Max { get; init; }

    public DateTimeOffset? MaxTime { get; init; }

    public double? Mean { get; init; }

    public double? StdDev { get; init; }

    public double? First { get; init; }

    public double? Last { get; init; }

    public double? Delta => First is { } a && Last is { } b ? b - a : null;

    /// <summary>Booleanos: porcentaje del tiempo a true.</summary>
    public double? TruePercent { get; init; }

    /// <summary>Booleanos: veces que pasa de false a true.</summary>
    public int RisingEdges { get; init; }

    public string MinText => WithTime(Min, MinTime);

    public string MaxText => WithTime(Max, MaxTime);

    public string MeanText => IsBoolean
        ? TruePercent is { } p ? $"{p.ToString("0.#", CultureInfo.InvariantCulture)} % true" : "—"
        : Value(Mean);

    public string StdDevText => IsBoolean ? "—" : Value(StdDev);

    public string FirstText => Value(First);

    public string LastText => Value(Last);

    public string DeltaText => IsBoolean ? "—" : Delta is { } d ? (d > 0 ? "+" : "") + Display.Format(d) : "—";

    public string SamplesText => Samples.ToString(CultureInfo.InvariantCulture);

    public string EdgesText => IsBoolean ? RisingEdges.ToString(CultureInfo.InvariantCulture) : "—";

    private string Value(double? value) =>
        value is not { } v ? "—" : IsBoolean ? (v >= 0.5 ? "true" : "false") : Display.Format(v);

    private string WithTime(double? value, DateTimeOffset? time) =>
        value is null ? "—" : time is { } t ? $"{Value(value)} · {t.ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture)}" : Value(value);
}

/// <summary>Cálculo de <see cref="SeriesStatsRow"/> y su exportación a CSV o al portapapeles.</summary>
public static class SeriesStats
{
    public const string CsvHeader = "tag,unit,from,to,samples,min,min_time,max,max_time,mean,stddev,first,last,delta,true_percent,rising_edges";

    /// <summary>
    /// Estadísticas de <paramref name="series"/> en [from, to]. En escalón cada valor vale hasta el siguiente punto (el
    /// último, hasta el final de la ventana); sin escalón, rectas entre puntos. Los huecos (calidad mala) no cuentan.
    /// </summary>
    public static SeriesStatsRow Compute(ChartSeries series, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(series);
        var row = new SeriesStatsRow(series.Name, series.ColorIndex, series.IsBoolean, series.Display);
        if (to < from)
            return row;

        // Tramo de la ventana: el valor que trae al empezar y los puntos de dentro.
        var points = new List<ChartPoint>();
        var first = series.LastIndexAtOrBefore(from);
        if (first >= 0 && series.ValueAt(from) is { } start)
            points.Add(new ChartPoint(from, start));
        var samples = first >= 0 && series.Points[first].Time == from && series.Points[first].Value is not null ? 1 : 0;
        for (var i = first + 1; i < series.Points.Count && series.Points[i].Time <= to; i++)
        {
            var p = series.Points[i];
            if (p.Value is not null)
                samples++;
            if (points.Count > 0 && points[^1].Time == p.Time)
                points[^1] = p;
            else
                points.Add(p);
        }
        var good = points.Where(p => p.Value is not null).ToList();
        if (good.Count == 0)
            return row with { Samples = samples };

        var min = good.MinBy(p => p.Value!.Value);
        var max = good.MaxBy(p => p.Value!.Value);
        // Referencia para que la varianza no pierda precisión con valores grandes.
        var shift = good[0].Value!.Value;
        double weight = 0, sum = 0, sumSquares = 0, trueTime = 0;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Value is not { } a)
                continue;
            var end = i + 1 < points.Count ? points[i + 1].Time : series.Step ? to : points[i].Time;
            var dt = (end - points[i].Time).TotalMilliseconds;
            if (dt <= 0)
                continue;
            double mean, meanSquare;
            if (series.Step || i + 1 >= points.Count || points[i + 1].Value is null)
            {
                if (!series.Step && (i + 1 >= points.Count || points[i + 1].Value is null))
                    continue; // sin escalón no hay recta hacia un hueco
                mean = a - shift;
                meanSquare = mean * mean;
            }
            else
            {
                // Recta de a a b: media (a + b) / 2 y media del cuadrado (a² + ab + b²) / 3.
                var (x, y) = (a - shift, points[i + 1].Value!.Value - shift);
                mean = (x + y) / 2;
                meanSquare = ((x * x) + (x * y) + (y * y)) / 3;
            }
            weight += dt;
            sum += mean * dt;
            sumSquares += meanSquare * dt;
            if (a >= 0.5)
                trueTime += dt;
        }

        double? average, deviation;
        if (weight > 0)
        {
            var m = sum / weight;
            average = m + shift;
            deviation = Math.Sqrt(Math.Max(0, (sumSquares / weight) - (m * m)));
        }
        else
        {
            // Todo en el mismo instante: media simple.
            average = good.Average(p => p.Value!.Value);
            deviation = 0;
        }

        var edges = 0;
        for (var i = 1; i < good.Count; i++)
        {
            if (good[i - 1].Value!.Value < 0.5 && good[i].Value!.Value >= 0.5)
                edges++;
        }

        return row with
        {
            Samples = samples,
            Min = min.Value,
            MinTime = min.Time,
            Max = max.Value,
            MaxTime = max.Time,
            Mean = average,
            StdDev = deviation,
            First = good[0].Value,
            Last = series.Step ? points[^1].Value : series.ValueAt(to) ?? good[^1].Value,
            TruePercent = series.IsBoolean ? weight > 0 ? trueTime * 100 / weight : good.Count(p => p.Value >= 0.5) * 100.0 / good.Count : null,
            RisingEdges = series.IsBoolean ? edges : 0,
        };
    }

    /// <summary>CSV con una fila por tag (números con punto, horas en UTC ISO 8601 como el del histórico).</summary>
    public static string ToCsv(IEnumerable<SeriesStatsRow> rows, DateTimeOffset from, DateTimeOffset to)
    {
        var text = new StringBuilder(CsvHeader).Append('\n');
        foreach (var r in rows)
        {
            string[] fields =
            [
                Quote(r.Name), Quote(r.Display.Unit), Time(from), Time(to), r.SamplesText,
                Number(r.Min), Time(r.MinTime), Number(r.Max), Time(r.MaxTime), Number(r.Mean), Number(r.IsBoolean ? null : r.StdDev),
                Number(r.First), Number(r.Last), Number(r.IsBoolean ? null : r.Delta), Number(r.TruePercent), r.IsBoolean ? r.EdgesText : "",
            ];
            text.AppendJoin(',', fields).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Tabla separada por tabuladores con los textos de la pantalla (para pegar en una hoja de cálculo).</summary>
    public static string ToTable(IEnumerable<SeriesStatsRow> rows)
    {
        var text = new StringBuilder("Tag\tMín\tMáx\tMedia\tσ\tPrimero\tÚltimo\tΔ\tMuestras\tFlancos ↑\n");
        foreach (var r in rows)
            text.AppendJoin('\t', r.Name, r.MinText, r.MaxText, r.MeanText, r.StdDevText, r.FirstText, r.LastText, r.DeltaText, r.SamplesText, r.EdgesText).Append('\n');
        return text.ToString();
    }

    private static string Number(double? value) => value is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "";

    private static string Time(DateTimeOffset? time) => time is { } t ? t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) : "";

    private static string Quote(string text) =>
        text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
}
