using System.Globalization;

namespace PlcMonitor.App.Charts;

/// <summary>Lectura de un tag en los cursores A y B: sus valores y el texto con la diferencia.</summary>
public sealed record CursorRow(string Name, int ColorIndex, double? A, double? B, string Text);

/// <summary>Cursores de medida de la tendencia: tiempo entre A y B y, para cada tag, su valor en los dos y la diferencia.</summary>
public static class CursorReadout
{
    /// <summary>Δt con signo (B − A) y precisión de milisegundos hasta 10 min: «+850 ms», «−12.345 s», «+1 min 02.156 s», «+2 h 07 min».</summary>
    public static string FormatDelta(TimeSpan delta)
    {
        var sign = delta < TimeSpan.Zero ? "−" : "+";
        var abs = delta.Duration();
        var culture = CultureInfo.InvariantCulture;
        if (abs < TimeSpan.FromSeconds(1))
            return $"{sign}{abs.TotalMilliseconds.ToString("0.#", culture)} ms";
        if (abs < TimeSpan.FromMinutes(1))
            return $"{sign}{abs.TotalSeconds.ToString("0.###", culture)} s";
        if (abs < TimeSpan.FromMinutes(10))
            return $"{sign}{(int)abs.TotalMinutes} min {(abs.TotalSeconds % 60).ToString("00.###", culture)} s";
        return sign + StateTimeline.FormatDuration(abs);
    }

    /// <summary>Una fila por tag con su valor en A, en B y la diferencia (los booleanos, sin diferencia).</summary>
    public static List<CursorRow> Rows(IEnumerable<ChartSeries> series, DateTimeOffset a, DateTimeOffset b) =>
        series.Select(s =>
        {
            var (va, vb) = (s.ValueAt(a), s.ValueAt(b));
            var text = $"A {s.Format(va)} · B {s.Format(vb)}";
            if (!s.IsBoolean && va is { } x && vb is { } y)
                text += $" · Δ {(y - x >= 0 ? "+" : "−")}{ChartAxis.FormatValue(Math.Abs(y - x))}";
            return new CursorRow(s.Name, s.ColorIndex, va, vb, text);
        }).ToList();
}
