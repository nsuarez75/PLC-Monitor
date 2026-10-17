using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.Controls;

/// <summary>
/// Gráfica con eje X numérico (ciclos superpuestos y X-Y), dibujada a mano como <see cref="TrendChart"/>. Las curvas
/// resaltadas van opacas y encima; las demás, tenues (persistencia de osciloscopio). Al pasar el ratón muestra las
/// coordenadas del cursor.
/// </summary>
public sealed class XYChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<XYCurve>?> CurvesProperty =
        AvaloniaProperty.Register<XYChart, IReadOnlyList<XYCurve>?>(nameof(Curves));

    /// <summary>Límites del eje Y; null = automático.</summary>
    public static readonly StyledProperty<double?> YMinProperty = AvaloniaProperty.Register<XYChart, double?>(nameof(YMin));

    public static readonly StyledProperty<double?> YMaxProperty = AvaloniaProperty.Register<XYChart, double?>(nameof(YMax));

    /// <summary>Texto del eje X (abajo a la derecha).</summary>
    public static readonly StyledProperty<string> XLabelProperty = AvaloniaProperty.Register<XYChart, string>(nameof(XLabel), "");

    /// <summary>Línea vertical en X = 0 (el instante del disparo en los ciclos).</summary>
    public static readonly StyledProperty<bool> ZeroLineProperty = AvaloniaProperty.Register<XYChart, bool>(nameof(ZeroLine));

    /// <summary>Bandas de tolerancia de los ciclos de referencia (detrás de las curvas).</summary>
    public static readonly StyledProperty<IReadOnlyList<ReferenceBand>?> BandsProperty =
        AvaloniaProperty.Register<XYChart, IReadOnlyList<ReferenceBand>?>(nameof(Bands));

    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<XYChart, int>(nameof(Revision));

    public static readonly StyledProperty<string> EmptyTextProperty = AvaloniaProperty.Register<XYChart, string>(nameof(EmptyText), "Sin datos");

    private const double LegendHeight = 26;
    private const double AxisFontSize = 11;
    private const double FadedOpacity = 0.28;
    private static readonly Typeface Font = new(FontFamily.Default);
    private Point? _hover;

    static XYChart()
    {
        AffectsRender<XYChart>(CurvesProperty, BandsProperty, YMinProperty, YMaxProperty, XLabelProperty, ZeroLineProperty, RevisionProperty, EmptyTextProperty);
    }

    public XYChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ClipToBounds = true;
    }

    public IReadOnlyList<XYCurve>? Curves
    {
        get => GetValue(CurvesProperty);
        set => SetValue(CurvesProperty, value);
    }

    public IReadOnlyList<ReferenceBand>? Bands
    {
        get => GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public double? YMin
    {
        get => GetValue(YMinProperty);
        set => SetValue(YMinProperty, value);
    }

    public double? YMax
    {
        get => GetValue(YMaxProperty);
        set => SetValue(YMaxProperty, value);
    }

    public string XLabel
    {
        get => GetValue(XLabelProperty);
        set => SetValue(XLabelProperty, value);
    }

    public bool ZeroLine
    {
        get => GetValue(ZeroLineProperty);
        set => SetValue(ZeroLineProperty, value);
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
        var text = Brush("App.Text", Brushes.Black);
        var muted = Brush("App.TextMuted", Brushes.Gray);
        var grid = new Pen(Brush("App.Border", Brushes.LightGray), 1);
        var curves = (Curves ?? []).Where(c => c.Points.Any(p => p.Y is not null)).ToArray();
        if (curves.Length == 0)
        {
            var empty = Text(EmptyText, muted, 13);
            context.DrawText(empty, new Point((bounds.Width - empty.Width) / 2, (bounds.Height - empty.Height) / 2));
            return;
        }

        // Rangos: X de los puntos con valor; Y automático o fijado a mano.
        var bands = Bands ?? [];
        var valued = curves.SelectMany(c => c.Points).Where(p => p.Y is not null)
            .Concat(bands.SelectMany(b => b.Reference.Where(p => p.Y is not null)
                .SelectMany(p => new[] { p with { Y = p.Y - b.Tolerance }, p with { Y = p.Y + b.Tolerance } })))
            .ToArray();
        var (xMin, xMax) = (valued.Min(p => p.X), valued.Max(p => p.X));
        if (ZeroLine)
            (xMin, xMax) = (Math.Min(xMin, 0), Math.Max(xMax, 0));
        var booleansOnly = curves.All(c => c.IsBoolean) && YMin is null && YMax is null;
        double yMin, yMax, yStep;
        if (booleansOnly)
        {
            (yMin, yMax, yStep) = (-0.15, 1.15, 1);
        }
        else
        {
            var ticks = Math.Max(2, (int)(bounds.Height / 55));
            (yMin, yMax, yStep) = ChartAxis.NiceRange(YMin ?? valued.Min(p => p.Y!.Value), YMax ?? valued.Max(p => p.Y!.Value), ticks);
            if (YMin is { } fixedMin)
                yMin = fixedMin;
            if (YMax is { } fixedMax)
                yMax = fixedMax;
            if (yMax <= yMin)
                yMax = yMin + 1;
        }

        var yTicks = new List<(double Value, FormattedText Label)>();
        var firstTick = booleansOnly ? 0 : Math.Ceiling((yMin - (yStep / 1000)) / yStep) * yStep;
        for (var v = firstTick; v <= yMax + (yStep / 1000); v += yStep)
        {
            var label = booleansOnly ? (v >= 0.5 ? "true" : "false") : ChartAxis.FormatValue(Math.Round(v / yStep) * yStep);
            yTicks.Add((v, Text(label, muted, AxisFontSize)));
        }
        var left = (yTicks.Count == 0 ? 20 : yTicks.Max(t => t.Label.Width)) + 12;
        var top = LegendHeight + 6;
        var plot = new Rect(left, top, Math.Max(10, bounds.Width - left - 12), Math.Max(10, bounds.Height - top - 24));
        var xTicksTarget = Math.Max(2, (int)(plot.Width / 80));
        var (x0, x1, xStep) = ChartAxis.NiceRange(xMin, xMax, xTicksTarget);
        double X(double v) => plot.Left + (plot.Width * (v - x0) / (x1 - x0));
        double Y(double v) => plot.Bottom - (plot.Height * (v - yMin) / (yMax - yMin));

        // Rejilla y etiquetas.
        foreach (var (value, label) in yTicks)
        {
            var y = Y(value);
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            context.DrawText(label, new Point(left - 8 - label.Width, y - (label.Height / 2)));
        }
        for (var v = x0; v <= x1 + (xStep / 1000); v += xStep)
        {
            var x = X(v);
            context.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = Text(ChartAxis.FormatValue(Math.Round(v / xStep) * xStep), muted, AxisFontSize);
            var lx = Math.Clamp(x - (label.Width / 2), plot.Left - 4, bounds.Width - label.Width - 2);
            context.DrawText(label, new Point(lx, plot.Bottom + 5));
        }
        if (ZeroLine && x0 <= 0 && x1 >= 0)
            context.DrawLine(new Pen(muted, 1.2, dashStyle: DashStyle.Dash), new Point(X(0), plot.Top), new Point(X(0), plot.Bottom));

        // Curvas: primero las bandas de referencia, luego las tenues y encima las resaltadas; las que se salen, en rojo.
        var bad = Brush("App.Bad", Brushes.Red);
        using (context.PushClip(plot.Inflate(2)))
        {
            foreach (var band in bands)
                DrawBand(context, band, X, Y, text);
            foreach (var curve in curves.OrderBy(c => c.Highlight).ThenBy(c => c.IsOut))
            {
                var color = curve.IsOut ? bad : Brush($"App.Chart{curve.ColorIndex % TrendChart.PaletteSize}", Brushes.SteelBlue);
                var brush = curve.Highlight ? color : curve.IsOut ? Faded(color, 0.55) : Faded(color);
                var pen = new Pen(brush, curve.Highlight ? 2 : 1.3, lineJoin: PenLineJoin.Round);
                foreach (var segment in Segments(curve, X, Y))
                {
                    if (segment.Count == 1)
                    {
                        context.DrawEllipse(brush, null, segment[0], 2, 2);
                        continue;
                    }
                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(segment[0], false);
                        for (var p = 1; p < segment.Count; p++)
                            g.LineTo(segment[p]);
                        g.EndFigure(false);
                    }
                    context.DrawGeometry(null, pen, geometry);
                }
                // El punto más reciente de la curva resaltada.
                if (curve.Highlight && curve.Points.LastOrDefault(p => p.Y is not null) is { Y: { } lastY } last)
                    context.DrawEllipse(brush, null, new Point(X(last.X), Y(lastY)), 3.5, 3.5);
            }
        }

        // Cursor: cruz y coordenadas.
        if (_hover is { } hover && plot.Contains(hover))
        {
            var dash = new Pen(muted, 1, dashStyle: DashStyle.Dash);
            context.DrawLine(dash, new Point(hover.X, plot.Top), new Point(hover.X, plot.Bottom));
            context.DrawLine(dash, new Point(plot.Left, hover.Y), new Point(plot.Right, hover.Y));
            var vx = x0 + ((x1 - x0) * (hover.X - plot.Left) / plot.Width);
            var vy = yMin + ((yMax - yMin) * (plot.Bottom - hover.Y) / plot.Height);
            var label = Text($"x {ChartAxis.FormatValue(Round(vx, xStep))}   y {ChartAxis.FormatValue(Round(vy, yStep))}", text, AxisFontSize);
            var box = new Rect(Math.Clamp(hover.X + 10, 0, bounds.Width - label.Width - 12), Math.Clamp(hover.Y - label.Height - 14, 0, bounds.Height - label.Height - 4),
                label.Width + 12, label.Height + 4);
            context.DrawRectangle(Brush("App.Surface", Brushes.White), new Pen(Brush("App.Border", Brushes.LightGray), 1), box, 4, 4);
            context.DrawText(label, new Point(box.X + 6, box.Y + 2));
        }

        // Texto del eje X, abajo a la derecha sobre la gráfica.
        if (XLabel.Length > 0)
        {
            var label = Text(XLabel, muted, AxisFontSize);
            context.DrawText(label, new Point(plot.Right - label.Width - 4, plot.Bottom - label.Height - 3));
        }

        DrawLegend(context, curves, text, muted, bounds.Width);
    }

    /// <summary>Leyenda: un elemento por tag con el último valor de su curva resaltada.</summary>
    private void DrawLegend(DrawingContext context, XYCurve[] curves, IBrush text, IBrush muted, double width)
    {
        double x = 4;
        foreach (var group in curves.GroupBy(c => (c.Name, c.ColorIndex)))
        {
            var latest = group.LastOrDefault(c => c.Highlight) ?? group.Last();
            var last = latest.Points.LastOrDefault(p => p.Y is not null).Y;
            var color = Brush($"App.Chart{group.Key.ColorIndex % TrendChart.PaletteSize}", Brushes.SteelBlue);
            var name = Text(group.Key.Name, muted, 12);
            var value = Text(last is not { } v ? "—" : latest.IsBoolean ? (v >= 0.5 ? "true" : "false") : ChartAxis.FormatValue(v), text, 12, FontWeight.SemiBold);
            var itemWidth = 14 + 6 + name.Width + 6 + value.Width + 18;
            if (x + itemWidth > width && x > 4)
            {
                context.DrawText(Text("…", muted, 12), new Point(x, 4));
                break;
            }
            context.DrawRectangle(color, null, new Rect(x, 10, 14, 3), 1.5, 1.5);
            context.DrawText(name, new Point(x + 20, 3));
            context.DrawText(value, new Point(x + 26 + name.Width, 3));
            x += itemWidth;
        }
    }

    /// <summary>Tramos en pantalla de una curva (un hueco la corta); en escalón, horizontal y luego vertical.</summary>
    internal static List<List<Point>> Segments(XYCurve curve, Func<double, double> x, Func<double, double> y)
    {
        var segments = new List<List<Point>>();
        List<Point>? current = null;
        foreach (var point in curve.Points)
        {
            if (point.Y is not { } value)
            {
                if (current is not null)
                    segments.Add(current);
                current = null;
                continue;
            }
            var p = new Point(x(point.X), y(value));
            if (current is null)
                current = [p];
            else
            {
                if (curve.Step)
                    current.Add(new Point(p.X, current[^1].Y));
                current.Add(p);
            }
        }
        if (current is not null)
            segments.Add(current);
        return segments;
    }

    /// <summary>Banda de una referencia: relleno entre referencia − tolerancia y + tolerancia, y la referencia discontinua.</summary>
    private void DrawBand(DrawingContext context, ReferenceBand band, Func<double, double> x, Func<double, double> y, IBrush text)
    {
        var color = Brush($"App.Chart{band.ColorIndex % TrendChart.PaletteSize}", Brushes.SteelBlue);
        var reference = new XYCurve(band.Name, band.Reference, band.ColorIndex, Step: band.Step);
        foreach (var segment in Segments(reference, x, y))
        {
            if (segment.Count < 2)
                continue;
            // Desplazamiento vertical en píxeles de la tolerancia (la escala es lineal).
            var dy = Math.Abs(y(band.Tolerance) - y(0));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(segment[0].X, segment[0].Y - dy), true);
                foreach (var p in segment.Skip(1))
                    g.LineTo(new Point(p.X, p.Y - dy));
                foreach (var p in Enumerable.Reverse(segment))
                    g.LineTo(new Point(p.X, p.Y + dy));
                g.EndFigure(true);
            }
            context.DrawGeometry(Faded(color, 0.14), null, geometry);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(segment[0], false);
                foreach (var p in segment.Skip(1))
                    g.LineTo(p);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(text, 1.4, dashStyle: new DashStyle([6, 3], 0)), line);
        }
    }

    private static double Round(double value, double step) => Math.Round(value / (step / 100)) * (step / 100);

    private static IBrush Faded(IBrush brush, double opacity = FadedOpacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static FormattedText Text(string text, IBrush brush, double size, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);
}
