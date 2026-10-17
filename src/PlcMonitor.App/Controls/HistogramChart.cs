using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.Controls;

/// <summary>
/// Histograma de cada tag (uno debajo de otro) con los valores de la ventana: barras por clase, curva normal, media,
/// ±3σ (discontinuas) y tolerancias (rojas), y arriba los estadísticos con Cp/Cpk (verde ≥ 1,33, ámbar ≥ 1, rojo por debajo).
/// </summary>
public sealed class HistogramChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<HistogramChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<DateTimeOffset?> FromProperty = AvaloniaProperty.Register<HistogramChart, DateTimeOffset?>(nameof(From));

    public static readonly StyledProperty<DateTimeOffset?> ToProperty = AvaloniaProperty.Register<HistogramChart, DateTimeOffset?>(nameof(To));

    /// <summary>Tolerancias inferior y superior (null = sin ella).</summary>
    public static readonly StyledProperty<double?> LowLimitProperty = AvaloniaProperty.Register<HistogramChart, double?>(nameof(LowLimit));

    public static readonly StyledProperty<double?> HighLimitProperty = AvaloniaProperty.Register<HistogramChart, double?>(nameof(HighLimit));

    /// <summary>Límites del eje de valores (X) fijados a mano; null = automático.</summary>
    public static readonly StyledProperty<double?> XMinProperty = AvaloniaProperty.Register<HistogramChart, double?>(nameof(XMin));

    public static readonly StyledProperty<double?> XMaxProperty = AvaloniaProperty.Register<HistogramChart, double?>(nameof(XMax));

    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<HistogramChart, int>(nameof(Revision));

    public static readonly StyledProperty<string> EmptyTextProperty = AvaloniaProperty.Register<HistogramChart, string>(nameof(EmptyText), "Sin datos");

    private const double AxisFontSize = 11;
    private const double HeaderHeight = 22;
    private static readonly Typeface Font = new(FontFamily.Default);
    private Point? _hover;

    static HistogramChart()
    {
        AffectsRender<HistogramChart>(SeriesProperty, FromProperty, ToProperty, LowLimitProperty, HighLimitProperty, XMinProperty, XMaxProperty, RevisionProperty, EmptyTextProperty);
    }

    public HistogramChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ClipToBounds = true;
    }

    public IReadOnlyList<ChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public DateTimeOffset? From
    {
        get => GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public DateTimeOffset? To
    {
        get => GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public double? LowLimit
    {
        get => GetValue(LowLimitProperty);
        set => SetValue(LowLimitProperty, value);
    }

    public double? HighLimit
    {
        get => GetValue(HighLimitProperty);
        set => SetValue(HighLimitProperty, value);
    }

    public double? XMin
    {
        get => GetValue(XMinProperty);
        set => SetValue(XMinProperty, value);
    }

    public double? XMax
    {
        get => GetValue(XMaxProperty);
        set => SetValue(XMaxProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public string EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 40 || bounds.Height < 40)
            return;
        // Fondo transparente: el ratón llega a toda la gráfica, también a las zonas sin dibujo.
        context.FillRectangle(Brushes.Transparent, bounds);
        var muted = Brush("App.TextMuted", Brushes.Gray);
        var from = From ?? DateTimeOffset.MinValue;
        var to = To ?? DateTimeOffset.MaxValue;
        var data = (Series ?? []).Where(s => !s.IsBoolean)
            .Select(s => (Series: s, Values: Histogram.Values(s, from, to)))
            .Where(d => d.Values.Count > 0).ToArray();
        if (data.Length == 0)
        {
            var text = (Series ?? []).Any(s => s.IsBoolean) && (Series ?? []).All(s => s.IsBoolean) ? "El histograma no sirve para booleanos" : EmptyText;
            var empty = Text(text, muted, 13);
            context.DrawText(empty, new Point((bounds.Width - empty.Width) / 2, (bounds.Height - empty.Height) / 2));
            return;
        }
        var height = bounds.Height / data.Length;
        for (var i = 0; i < data.Length; i++)
            DrawOne(context, data[i].Series, data[i].Values, new Rect(0, i * height, bounds.Width, height - (i < data.Length - 1 ? 10 : 0)));
    }

    private void DrawOne(DrawingContext context, ChartSeries series, List<double> values, Rect area)
    {
        var text = Brush("App.Text", Brushes.Black);
        var muted = Brush("App.TextMuted", Brushes.Gray);
        var bad = Brush("App.Bad", Brushes.Red);
        var grid = new Pen(Brush("App.Border", Brushes.LightGray), 1);
        var color = Brush($"App.Chart{series.ColorIndex % TrendChart.PaletteSize}", Brushes.SteelBlue);
        var stats = Histogram.Stats(values, LowLimit, HighLimit)!;
        var bins = Histogram.Bins(values);

        // Cabecera: nombre y estadísticos.
        var x = area.Left + 4;
        context.DrawRectangle(color, null, new Rect(x, area.Top + 9, 14, 3), 1.5, 1.5);
        x += 20;
        var name = Text(series.Name, text, 12, FontWeight.SemiBold);
        context.DrawText(name, new Point(x, area.Top + 2));
        x += name.Width + 14;
        string F(double v) => ChartAxis.FormatValue(Math.Round(v, 4));
        var parts = new List<(string Text, IBrush Brush)>
        {
            ($"n {stats.Count}", muted), ($"media {F(stats.Mean)}", muted), ($"σ {F(stats.StdDev)}", muted),
            ($"mín {F(stats.Min)}", muted), ($"máx {F(stats.Max)}", muted),
        };
        if (stats.Cp is { } cp)
            parts.Add(($"Cp {cp.ToString("0.00", CultureInfo.InvariantCulture)}", CapabilityBrush(cp)));
        if (stats.Cpk is { } cpk)
            parts.Add(($"Cpk {cpk.ToString("0.00", CultureInfo.InvariantCulture)}", CapabilityBrush(cpk)));
        foreach (var (label, brush) in parts)
        {
            var t = Text(label, brush, 12, brush == muted ? FontWeight.Normal : FontWeight.SemiBold);
            if (x + t.Width > area.Right - 4)
                break;
            context.DrawText(t, new Point(x, area.Top + 2));
            x += t.Width + 12;
        }

        // Rango de valores: clases, ±3σ y tolerancias (o lo fijado a mano).
        var edges = new List<double> { bins[0].From, bins[^1].To, stats.Mean - (3 * stats.StdDev), stats.Mean + (3 * stats.StdDev) };
        if (LowLimit is { } low)
            edges.Add(low);
        if (HighLimit is { } high)
            edges.Add(high);
        var (x0, x1, xStep) = ChartAxis.NiceRange(XMin ?? edges.Min(), XMax ?? edges.Max(), Math.Max(2, (int)(area.Width / 90)));
        if (XMin is { } fixedMin)
            x0 = fixedMin;
        if (XMax is { } fixedMax)
            x1 = fixedMax;
        if (x1 <= x0)
            x1 = x0 + 1;
        var maxCount = bins.Max(b => b.Count);
        var (_, yMax, yStep) = ChartAxis.NiceRange(0, Math.Max(1, maxCount), Math.Max(2, (int)((area.Height - HeaderHeight) / 45)));
        yStep = Math.Max(1, yStep);
        var labelWidth = Text(ChartAxis.FormatValue(yMax), muted, AxisFontSize).Width + 12;
        var plot = new Rect(area.Left + labelWidth, area.Top + HeaderHeight + 6, Math.Max(10, area.Width - labelWidth - 12), Math.Max(10, area.Height - HeaderHeight - 6 - 22));
        double X(double v) => plot.Left + (plot.Width * (v - x0) / (x1 - x0));
        double Y(double v) => plot.Bottom - (plot.Height * v / yMax);

        for (var v = 0.0; v <= yMax + 1e-9; v += yStep)
        {
            context.DrawLine(grid, new Point(plot.Left, Y(v)), new Point(plot.Right, Y(v)));
            var label = Text(ChartAxis.FormatValue(v), muted, AxisFontSize);
            context.DrawText(label, new Point(plot.Left - 8 - label.Width, Y(v) - (label.Height / 2)));
        }
        for (var v = Math.Ceiling(x0 / xStep) * xStep; v <= x1 + (xStep / 1000); v += xStep)
        {
            var label = Text(ChartAxis.FormatValue(Math.Round(v / xStep) * xStep), muted, AxisFontSize);
            context.DrawText(label, new Point(Math.Clamp(X(v) - (label.Width / 2), plot.Left - 4, area.Right - label.Width - 2), plot.Bottom + 4));
        }

        HistogramBin? hovered = null;
        using (context.PushClip(plot.Inflate(1)))
        {
            var fill = color is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.5) : color;
            foreach (var bin in bins)
            {
                var rect = new Rect(new Point(X(bin.From) + 0.5, Y(bin.Count)), new Point(X(bin.To) - 0.5, plot.Bottom));
                context.DrawRectangle(fill, new Pen(color, 1), rect);
                if (_hover is { } h && h.X >= X(bin.From) && h.X < X(bin.To) && h.Y >= plot.Top && h.Y <= plot.Bottom)
                    hovered = bin;
            }

            // Curva normal con la media y la desviación, a la escala de las clases.
            if (stats.StdDev > 0)
            {
                var width = bins[0].To - bins[0].From;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    for (var k = 0; k <= 120; k++)
                    {
                        var v = x0 + ((x1 - x0) * k / 120);
                        var z = (v - stats.Mean) / stats.StdDev;
                        var count = stats.Count * width * Math.Exp(-0.5 * z * z) / (stats.StdDev * Math.Sqrt(2 * Math.PI));
                        var p = new Point(X(v), Y(count));
                        if (k == 0)
                            g.BeginFigure(p, false);
                        else
                            g.LineTo(p);
                    }
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(text, 1.4), geometry);
            }
            context.DrawLine(new Pen(text, 1.6), new Point(X(stats.Mean), plot.Top), new Point(X(stats.Mean), plot.Bottom));
            if (stats.StdDev > 0)
            {
                var dotted = new Pen(muted, 1.5, dashStyle: new DashStyle([4, 3], 0));
                foreach (var v in new[] { stats.Mean - (3 * stats.StdDev), stats.Mean + (3 * stats.StdDev) })
                    context.DrawLine(dotted, new Point(X(v), plot.Top), new Point(X(v), plot.Bottom));
            }
        }
        foreach (var (limit, label) in new[] { (LowLimit, "LIE"), (HighLimit, "LSE") })
        {
            if (limit is not { } l || l < x0 || l > x1)
                continue;
            context.DrawLine(new Pen(bad, 1.8), new Point(X(l), plot.Top), new Point(X(l), plot.Bottom));
            var t = Text($"{label} {ChartAxis.FormatValue(l)}", bad, AxisFontSize, FontWeight.SemiBold);
            context.DrawText(t, new Point(Math.Clamp(X(l) + 4, plot.Left, plot.Right - t.Width), plot.Top + 2));
        }

        if (hovered is { } bin2 && _hover is { } hover)
        {
            var share = bin2.Count * 100.0 / stats.Count;
            var label = Text($"[{F(bin2.From)}, {F(bin2.To)}): {bin2.Count} ({share.ToString("0.#", CultureInfo.InvariantCulture)} %)", text, AxisFontSize);
            var box = new Rect(Math.Clamp(hover.X + 10, area.Left, area.Right - label.Width - 12), Math.Clamp(hover.Y - label.Height - 14, area.Top, area.Bottom - label.Height - 4),
                label.Width + 12, label.Height + 4);
            context.DrawRectangle(Brush("App.Surface", Brushes.White), grid, box, 4, 4);
            context.DrawText(label, new Point(box.X + 6, box.Y + 2));
        }
    }

    /// <summary>Color de Cp/Cpk: verde si es capaz (≥ 1,33), ámbar justo (≥ 1), rojo si no.</summary>
    private IBrush CapabilityBrush(double value) =>
        value >= 1.33 ? Brush("App.Good", Brushes.Green) : value >= 1 ? Brush("App.Warn", Brushes.Orange) : Brush("App.Bad", Brushes.Red);

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static FormattedText Text(string text, IBrush brush, double size, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);
}
