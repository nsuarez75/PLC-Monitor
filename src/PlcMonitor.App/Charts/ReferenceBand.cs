namespace PlcMonitor.App.Charts;

/// <summary>Banda de tolerancia alrededor del ciclo de referencia de un tag, para dibujarla y comprobar ciclos.</summary>
/// <param name="Reference">Ciclo de referencia (X = segundos desde el disparo).</param>
/// <param name="Tolerance">Margen ± en unidades del tag.</param>
public sealed record ReferenceBand(string Name, int ColorIndex, IReadOnlyList<XYPoint> Reference, double Tolerance, bool Step);

/// <summary>
/// Ciclo de referencia con banda de tolerancia: un ciclo «bueno» se fija como patrón y cada ciclo nuevo se compara con
/// él punto a punto en el tramo común; si en algún punto se aleja más que la tolerancia, el ciclo está fuera.
/// </summary>
public static class ReferenceCycles
{
    /// <summary>Puntos como mucho que se guardan de una referencia.</summary>
    public const int MaxStoredPoints = 2000;

    /// <summary>
    /// Margen ± en unidades del tag: el valor tal cual o, en porcentaje, ese % del rango de la referencia (máx − mín), que
    /// funciona también con señales que pasan por cero.
    /// </summary>
    public static double Tolerance(IReadOnlyList<XYPoint> reference, double value, bool percent)
    {
        if (!percent)
            return Math.Abs(value);
        var values = reference.Where(p => p.Y is not null).Select(p => p.Y!.Value).ToArray();
        var span = values.Length == 0 ? 0 : values.Max() - values.Min();
        return Math.Abs(value) / 100 * (span > 0 ? span : Math.Max(1, values.Select(Math.Abs).DefaultIfEmpty(1).Max()));
    }

    /// <summary>Valor de una curva en X: el del último punto anterior (escalón) o la recta entre los dos que lo rodean.</summary>
    public static double? ValueAt(IReadOnlyList<XYPoint> curve, double x, bool step)
    {
        int lo = 0, hi = curve.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (curve[mid].X <= x)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (found < 0 || curve[found].Y is not { } y)
            return null;
        if (step || found == curve.Count - 1 || curve[found + 1].Y is not { } next)
            return y;
        var (a, b) = (curve[found].X, curve[found + 1].X);
        return b <= a ? y : y + ((next - y) * (x - a) / (b - a));
    }

    /// <summary>
    /// ¿Se mantiene el ciclo dentro de la banda? Se compara en el tramo de X común, en los puntos de los dos (así no se
    /// escapa un escalón de ninguno). Un ciclo sin tramo común no se considera fuera.
    /// </summary>
    public static bool IsInside(IReadOnlyList<XYPoint> cycle, ReferenceBand band, bool cycleStep)
    {
        var reference = band.Reference;
        var withValues = cycle.Where(p => p.Y is not null).ToArray();
        var referenceValues = reference.Where(p => p.Y is not null).ToArray();
        if (withValues.Length == 0 || referenceValues.Length == 0)
            return true;
        var from = Math.Max(withValues[0].X, referenceValues[0].X);
        var to = Math.Min(withValues[^1].X, referenceValues[^1].X);
        if (to < from)
            return true;
        var tolerance = band.Tolerance + 1e-9;
        foreach (var x in withValues.Select(p => p.X).Concat(referenceValues.Select(p => p.X)).Where(x => x >= from && x <= to))
        {
            if (ValueAt(cycle, x, cycleStep) is { } y && ValueAt(reference, x, band.Step) is { } r && Math.Abs(y - r) > tolerance)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Reduce una curva a como mucho <see cref="MaxStoredPoints"/> puntos para guardarla: por tramos de X, el primero, el
    /// mínimo, el máximo y el último, en orden (conserva picos y escalones).
    /// </summary>
    public static List<XYPoint> Simplify(IReadOnlyList<XYPoint> curve)
    {
        var points = curve.Where(p => p.Y is not null).ToList();
        if (points.Count <= MaxStoredPoints)
            return points;
        var buckets = MaxStoredPoints / 4;
        var (x0, x1) = (points[0].X, points[^1].X);
        var width = (x1 - x0) / buckets;
        var result = new List<XYPoint>(MaxStoredPoints);
        foreach (var group in points.GroupBy(p => width <= 0 ? 0 : Math.Min(buckets - 1, (int)((p.X - x0) / width))))
        {
            var g = group.ToList();
            var keep = new[] { g[0], g.MinBy(p => p.Y)!, g.MaxBy(p => p.Y)!, g[^1] }.Distinct().OrderBy(p => p.X);
            result.AddRange(keep);
        }
        return result;
    }
}
