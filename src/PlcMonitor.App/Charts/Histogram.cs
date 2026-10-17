namespace PlcMonitor.App.Charts;

/// <summary>Estadísticos de un histograma. Cp y Cpk solo con desviación y las tolerancias que hagan falta.</summary>
public sealed record HistogramStats(int Count, double Mean, double StdDev, double Min, double Max, double? Cp, double? Cpk);

/// <summary>Clase del histograma: valores en [From, To) (la última incluye su final).</summary>
public readonly record struct HistogramBin(double From, double To, int Count);

/// <summary>Histograma de valores con media, desviación típica y capacidad del proceso (Cp/Cpk).</summary>
public static class Histogram
{
    /// <summary>Valores con calidad buena de la serie recibidos en [from, to].</summary>
    public static List<double> Values(ChartSeries series, DateTimeOffset from, DateTimeOffset to)
    {
        var values = new List<double>();
        var first = series.LastIndexAtOrBefore(from);
        if (first < 0 || series.Points[first].Time < from)
            first++;
        for (var i = first; i < series.Points.Count && series.Points[i].Time <= to; i++)
        {
            if (series.Points[i].Value is { } v)
                values.Add(v);
        }
        return values;
    }

    /// <summary>
    /// Media, desviación típica muestral, extremos y, con tolerancias, Cp = (sup − inf) / 6σ y Cpk = la menor distancia de
    /// la media a una tolerancia / 3σ (con una sola tolerancia, solo Cpk). Null sin valores.
    /// </summary>
    public static HistogramStats? Stats(IReadOnlyList<double> values, double? low, double? high)
    {
        if (values.Count == 0)
            return null;
        var mean = values.Average();
        var sigma = values.Count < 2 ? 0 : Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
        double? cp = null, cpk = null;
        if (sigma > 0)
        {
            if (low is { } l && high is { } h && h > l)
                cp = (h - l) / (6 * sigma);
            var sides = new List<double>();
            if (high is { } hi)
                sides.Add((hi - mean) / (3 * sigma));
            if (low is { } lo)
                sides.Add((mean - lo) / (3 * sigma));
            if (sides.Count > 0)
                cpk = sides.Min();
        }
        return new HistogramStats(values.Count, mean, sigma, values.Min(), values.Max(), cp, cpk);
    }

    /// <summary>Clases de ancho «redondo» (1, 2, 5 × 10ⁿ), unas √n entre 5 y 40, que cubren todos los valores.</summary>
    public static List<HistogramBin> Bins(IReadOnlyList<double> values)
    {
        var bins = new List<HistogramBin>();
        if (values.Count == 0)
            return bins;
        var (min, max) = (values.Min(), values.Max());
        var target = Math.Clamp((int)Math.Ceiling(Math.Sqrt(values.Count)), 5, 40);
        var width = max - min < 1e-12 ? ChartAxis.NiceStep(Math.Max(Math.Abs(min) * 0.01, 1e-6)) : ChartAxis.NiceStep((max - min) / target);
        var start = Math.Floor(min / width) * width;
        var count = Math.Max(1, (int)Math.Floor(((max - start) / width) + 1e-9) + 1);
        var counts = new int[count];
        foreach (var v in values)
            counts[Math.Clamp((int)Math.Floor(((v - start) / width) + 1e-9), 0, count - 1)]++;
        for (var i = 0; i < count; i++)
            bins.Add(new HistogramBin(start + (i * width), start + ((i + 1) * width), counts[i]));
        return bins;
    }
}
