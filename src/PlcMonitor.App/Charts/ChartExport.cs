using System.Globalization;

namespace PlcMonitor.App.Charts;

/// <summary>Textos de la imagen exportada de una gráfica: cabecera y nombre del fichero.</summary>
public static class ChartExport
{
    /// <summary>Periodo visible en hora local («17/10/2026 06:00:00 – 14:00:00» si es el mismo día).</summary>
    public static string Period(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not { } f || to is not { } t)
            return "";
        var (a, b) = (f.ToLocalTime(), t.ToLocalTime());
        var start = a.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var end = b.ToString(a.Date == b.Date ? "HH:mm:ss" : "dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        return $"{start} – {end}";
    }

    /// <summary>Línea bajo el título: periodo y de dónde salen los datos.</summary>
    public static string Subtitle(ChartViewModel chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var parts = new List<string>();
        if (chart.Kind is not (ChartKind.Cycles or ChartKind.XY) && Period(chart.From, chart.To) is { Length: > 0 } period)
            parts.Add(period);
        if (chart.Source.Length > 0)
            parts.Add(chart.Source);
        return string.Join(" · ", parts);
    }

    /// <summary>Nombre propuesto para el fichero: el título sin caracteres raros y la fecha y hora de ahora.</summary>
    public static string FileName(string title, DateTime now, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string((title ?? "").Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim('.', ' ');
        if (clean.Length == 0)
            clean = "grafica";
        return $"{clean}-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.{extension}";
    }
}
