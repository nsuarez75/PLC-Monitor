using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.Charts;

/// <summary>Tramo de tiempo en que una tendencia de disparo muestra valores; <see cref="To"/> null = sigue abierto.</summary>
public readonly record struct TriggerSpan(DateTimeOffset From, DateTimeOffset? To);

/// <summary>
/// Tendencia de disparo (como el histórico por disparo del agente): los tags solo se ven mientras el tag disparo cumple
/// la condición, con un tiempo antes (pre-disparo, como un osciloscopio) y otro después de que deje de cumplirse.
/// Trabaja sobre los puntos en vivo que guarda la app (la última hora).
/// </summary>
public static class TriggerFilter
{
    /// <summary>¿Cumple <paramref name="value"/> la condición? Booleanos como 0/1; un hueco (calidad mala) no la cumple.</summary>
    public static bool IsMet(double? value, TriggerCondition condition, double target) => value is { } v && condition switch
    {
        TriggerCondition.Equal => v == target,
        TriggerCondition.NotEqual => v != target,
        TriggerCondition.Greater => v > target,
        TriggerCondition.GreaterOrEqual => v >= target,
        TriggerCondition.Less => v < target,
        TriggerCondition.LessOrEqual => v <= target,
        _ => false,
    };

    /// <summary>Número del valor de comparación: true/false como 1/0, decimales con punto o coma.</summary>
    public static bool TryParseTarget(string text, out double target)
    {
        text = text.Trim();
        if (bool.TryParse(text, out var b))
        {
            target = b ? 1 : 0;
            return true;
        }
        return double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out target);
    }

    /// <summary>
    /// Tramos activos: desde <paramref name="before"/> antes de que se cumpla hasta <paramref name="hold"/> después de que
    /// deje de cumplirse (unidos si se solapan).
    /// </summary>
    public static List<TriggerSpan> Spans(IReadOnlyList<ChartPoint> trigger, Func<double?, bool> met, TimeSpan hold, TimeSpan before = default)
    {
        var spans = new List<TriggerSpan>();
        DateTimeOffset? start = null;
        foreach (var point in trigger)
        {
            var on = met(point.Value);
            if (on && start is null)
            {
                // Si el nuevo tramo (con su tiempo previo) pisa el anterior y su tiempo extra, sigue el mismo tramo.
                var from = point.Time - before;
                if (spans.Count > 0 && spans[^1].To is { } end && from <= end)
                {
                    start = spans[^1].From;
                    spans.RemoveAt(spans.Count - 1);
                }
                else
                {
                    start = from;
                }
            }
            else if (!on && start is { } opened)
            {
                spans.Add(new TriggerSpan(opened, point.Time + hold));
                start = null;
            }
        }
        if (start is { } open)
            spans.Add(new TriggerSpan(open, null));
        return spans;
    }

    /// <summary>
    /// Deja en <paramref name="output"/> los puntos de <paramref name="points"/> dentro de los tramos: al empezar cada uno, el
    /// valor que tenía el tag en ese momento (salvo con <paramref name="includeStart"/> = false: el histograma solo cuenta
    /// muestras de verdad); al acabar, un hueco para que la línea se corte.
    /// </summary>
    public static void Apply(IReadOnlyList<ChartPoint> points, IReadOnlyList<TriggerSpan> spans, DateTimeOffset now, List<ChartPoint> output, bool includeStart = true)
    {
        output.Clear();
        if (points.Count == 0)
            return;
        var series = new ChartSeries("", points);
        foreach (var span in spans)
        {
            // Tramo cerrado: hasta su final. Abierto (o aún en su tiempo extra): todo lo recibido, aunque la hora del PLC
            // vaya algo por delante del reloj de este equipo.
            var end = span.To is { } to && to < now ? to : DateTimeOffset.MaxValue;
            var first = series.LastIndexAtOrBefore(span.From);
            if (first >= 0 && includeStart && points[first].Time < span.From)
                output.Add(new ChartPoint(span.From, points[first].Value));
            else if (first >= 0 && points[first].Time >= span.From)
                output.Add(points[first]);
            for (var i = Math.Max(first + 1, 0); i < points.Count && points[i].Time < end; i++)
                output.Add(points[i]);
            if (span.To is { } closed && closed < now)
                output.Add(new ChartPoint(closed, null));
        }
    }
}
