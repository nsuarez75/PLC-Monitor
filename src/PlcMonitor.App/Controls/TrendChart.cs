using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.Controls;

/// <summary>
/// Gráfica de tendencia (valor frente a tiempo) dibujada a mano: sin librerías de terceros y con los colores del tema
/// (App.Chart0…7). Leyenda arriba con el último valor de cada serie o, al pasar el ratón, el valor en ese instante.
/// Para redibujar tras añadir puntos a las series basta con cambiar <see cref="Revision"/>.
/// </summary>
public sealed class TrendChart : Control
{
    public const int PaletteSize = 8;

    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<TrendChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    /// <summary>Inicio de la ventana; null = el primer punto.</summary>
    public static readonly StyledProperty<DateTimeOffset?> FromProperty = AvaloniaProperty.Register<TrendChart, DateTimeOffset?>(nameof(From));

    /// <summary>Fin de la ventana; null = el último punto.</summary>
    public static readonly StyledProperty<DateTimeOffset?> ToProperty = AvaloniaProperty.Register<TrendChart, DateTimeOffset?>(nameof(To));

    /// <summary>Límites del eje Y; null = automático (se ajusta a lo visible).</summary>
    public static readonly StyledProperty<double?> YMinProperty = AvaloniaProperty.Register<TrendChart, double?>(nameof(YMin));

    public static readonly StyledProperty<double?> YMaxProperty = AvaloniaProperty.Register<TrendChart, double?>(nameof(YMax));

    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<TrendChart, int>(nameof(Revision));

    public static readonly StyledProperty<string> EmptyTextProperty = AvaloniaProperty.Register<TrendChart, string>(nameof(EmptyText), "Sin datos");

    /// <summary>Muestra la leyenda (en la mini tendencia del detalle de tag sobra).</summary>
    public static readonly StyledProperty<bool> ShowLegendProperty = AvaloniaProperty.Register<TrendChart, bool>(nameof(ShowLegend), true);

    private const double LegendHeight = 26;
    private const double AxisFontSize = 11;
    private static readonly Typeface Font = new(FontFamily.Default);
    private Point? _hover;
    private double? _dragStart;
    private Rect _plot;
    private (DateTimeOffset From, DateTimeOffset To)? _window;

    static TrendChart()
    {
        AffectsRender<TrendChart>(SeriesProperty, FromProperty, ToProperty, YMinProperty, YMaxProperty, RevisionProperty, EmptyTextProperty, ShowLegendProperty);
    }

    public TrendChart()
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

    /// <summary>El usuario ha seleccionado un tramo arrastrando con el ratón (zoom en el tiempo).</summary>
    public event EventHandler<ChartZoomEventArgs>? ZoomRequested;

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

    public bool ShowLegend
    {
        get => GetValue(ShowLegendProperty);
        set => SetValue(ShowLegendProperty, value);
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !_plot.Contains(e.GetPosition(this)))
            return;
        _dragStart = e.GetPosition(this).X;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is not { } start)
            return;
        _dragStart = null;
        e.Pointer.Capture(null);
        var end = Math.Clamp(e.GetPosition(this).X, _plot.Left, _plot.Right);
        // Un clic sin arrastrar no hace zoom.
        if (Math.Abs(end - start) >= 6 && _window is { } window)
        {
            var (from, to) = window;
            DateTimeOffset At(double x) => from + TimeSpan.FromMilliseconds((to - from).TotalMilliseconds * (x - _plot.Left) / _plot.Width);
            ZoomRequested?.Invoke(this, new ChartZoomEventArgs(At(Math.Min(start, end)), At(Math.Max(start, end))));
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 40 || bounds.Height < 40)
            return;
        var text = Brush("App.Text", Brushes.Black);
        var muted = Brush("App.TextMuted", Brushes.Gray);
        var grid = new Pen(Brush("App.Border", Brushes.LightGray), 1);
        var series = (Series ?? []).Where(s => s.Points.Count > 0).ToArray();
        _window = null;
        var withValues = series.Where(s => s.Points.Any(p => p.Value is not null)).ToArray();
        if (withValues.Length == 0)
        {
            var empty = Text(EmptyText, muted, 13);
            context.DrawText(empty, new Point((bounds.Width - empty.Width) / 2, (bounds.Height - empty.Height) / 2));
            return;
        }

        // Ventana de tiempo y rango de valores (solo lo visible).
        var from = From ?? withValues.Min(s => s.Points[0].Time);
        var to = To ?? withValues.Max(s => s.Points[^1].Time);
        if (to <= from)
            to = from + TimeSpan.FromSeconds(1);
        var (min, max) = ValueRange(withValues, from, to);
        var booleansOnly = withValues.All(s => s.IsBoolean) && YMin is null && YMax is null;
        double yMin, yMax, yStep;
        if (booleansOnly)
        {
            (yMin, yMax, yStep) = (-0.15, 1.15, 1);
        }
        else
        {
            var ticks = Math.Max(2, (int)(bounds.Height / 55));
            (yMin, yMax, yStep) = ChartAxis.NiceRange(YMin ?? min, YMax ?? max, ticks);
            // Límites fijados a mano: se respetan tal cual (las marcas siguen siendo redondas).
            if (YMin is { } fixedMin)
                yMin = fixedMin;
            if (YMax is { } fixedMax)
                yMax = fixedMax;
            if (yMax <= yMin)
                yMax = yMin + 1;
        }

        // Marcas del eje Y y su ancho.
        var yTicks = new List<(double Value, FormattedText Label)>();
        var firstTick = booleansOnly ? 0 : Math.Ceiling((yMin - (yStep / 1000)) / yStep) * yStep;
        for (var v = firstTick; v <= yMax + (yStep / 1000); v += yStep)
        {
            var label = booleansOnly ? (v >= 0.5 ? "true" : "false") : ChartAxis.FormatValue(Math.Round(v / yStep) * yStep);
            yTicks.Add((v, Text(label, muted, AxisFontSize)));
        }
        var left = (yTicks.Count == 0 ? 20 : yTicks.Max(t => t.Label.Width)) + 12;
        var top = ShowLegend ? LegendHeight + 6 : 8;
        var plot = new Rect(left, top, Math.Max(10, bounds.Width - left - 12), Math.Max(10, bounds.Height - top - 24));
        _plot = plot;
        _window = (from, to);
        double X(DateTimeOffset t) => plot.Left + (plot.Width * (t - from).TotalMilliseconds / (to - from).TotalMilliseconds);
        double Y(double v) => plot.Bottom - (plot.Height * (v - yMin) / (yMax - yMin));

        // Rejilla y etiquetas.
        foreach (var (value, label) in yTicks)
        {
            var y = Y(value);
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            context.DrawText(label, new Point(left - 8 - label.Width, y - (label.Height / 2)));
        }
        var span = to - from;
        var step = ChartAxis.TimeStep(span, Math.Max(2, (int)(plot.Width / 95)));
        foreach (var tick in ChartAxis.TimeTicks(from, to, step))
        {
            var x = X(tick);
            context.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = Text(ChartAxis.FormatTime(tick, step, span), muted, AxisFontSize);
            var lx = Math.Clamp(x - (label.Width / 2), plot.Left - 4, bounds.Width - label.Width - 2);
            context.DrawText(label, new Point(lx, plot.Bottom + 5));
        }

        // Series.
        using (context.PushClip(plot.Inflate(2)))
        {
            for (var i = 0; i < series.Length; i++)
            {
                var pen = new Pen(Brush($"App.Chart{series[i].ColorIndex % PaletteSize}", Brushes.SteelBlue), 1.8, lineJoin: PenLineJoin.Round);
                foreach (var segment in Segments(series[i], from, to, plot.Width, X, Y))
                {
                    if (segment.Count == 1)
                    {
                        context.DrawEllipse(pen.Brush, null, segment[0], 2, 2);
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
            }
        }

        // Tramo que se está seleccionando para el zoom.
        if (_dragStart is { } dragStart && _hover is { } dragNow)
        {
            var x1 = Math.Clamp(Math.Min(dragStart, dragNow.X), plot.Left, plot.Right);
            var x2 = Math.Clamp(Math.Max(dragStart, dragNow.X), plot.Left, plot.Right);
            context.DrawRectangle(new SolidColorBrush(Colors.Gray, 0.15), null, new Rect(x1, plot.Top, x2 - x1, plot.Height));
        }

        // Cursor: línea vertical y hora.
        DateTimeOffset? at = null;
        if (_hover is { } hover && plot.Contains(hover))
        {
            at = from + TimeSpan.FromMilliseconds((to - from).TotalMilliseconds * (hover.X - plot.Left) / plot.Width);
            context.DrawLine(new Pen(muted, 1, dashStyle: DashStyle.Dash), new Point(hover.X, plot.Top), new Point(hover.X, plot.Bottom));
            var label = Text(at.Value.ToLocalTime().ToString(span > TimeSpan.FromDays(1) ? "dd/MM HH:mm:ss" : "HH:mm:ss", CultureInfo.InvariantCulture), text, AxisFontSize);
            var box = new Rect(Math.Clamp(hover.X - (label.Width / 2) - 6, 0, bounds.Width - label.Width - 12), plot.Bottom + 2, label.Width + 12, label.Height + 4);
            context.DrawRectangle(Brush("App.Surface", Brushes.White), new Pen(Brush("App.Border", Brushes.LightGray), 1), box, 4, 4);
            context.DrawText(label, new Point(box.X + 6, box.Y + 2));
        }

        if (ShowLegend)
            DrawLegend(context, series, at ?? to, text, muted, bounds.Width);
    }

    /// <summary>Leyenda: color, nombre y valor (al final de la ventana o bajo el cursor).</summary>
    private void DrawLegend(DrawingContext context, ChartSeries[] series, DateTimeOffset at, IBrush text, IBrush muted, double width)
    {
        double x = 4;
        foreach (var s in series)
        {
            var color = Brush($"App.Chart{s.ColorIndex % PaletteSize}", Brushes.SteelBlue);
            var name = Text(s.Name, muted, 12);
            var value = Text(s.Format(s.ValueAt(at)), text, 12, FontWeight.SemiBold);
            var itemWidth = 14 + 6 + name.Width + 6 + value.Width + 18;
            if (x + itemWidth > width && x > 4)
            {
                var more = Text("…", muted, 12);
                context.DrawText(more, new Point(x, 4));
                break;
            }
            context.DrawRectangle(color, null, new Rect(x, 10, 14, 3), 1.5, 1.5);
            context.DrawText(name, new Point(x + 20, 3));
            context.DrawText(value, new Point(x + 26 + name.Width, 3));
            x += itemWidth;
        }
    }

    /// <summary>
    /// Tramos de puntos en pantalla (un hueco de calidad mala corta la línea). Con más puntos que píxeles se reduce a
    /// primero/mínimo/máximo/último por columna, que conserva los picos.
    /// </summary>
    internal static List<List<Point>> Segments(ChartSeries series, DateTimeOffset from, DateTimeOffset to, double width,
        Func<DateTimeOffset, double> x, Func<double, double> y)
    {
        var segments = new List<List<Point>>();
        var points = series.Points;
        var start = Math.Max(0, series.LastIndexAtOrBefore(from));
        var end = series.LastIndexAtOrBefore(to);
        if (end < 0)
            return segments;
        if (!series.Step && end < points.Count - 1)
            end++; // la recta sigue hasta el primer punto fuera de la ventana
        var decimate = end - start + 1 > width * 3;
        List<Point>? current = null;
        double? lastY = null;
        var column = int.MinValue;
        double colMin = 0, colMax = 0, colLast = 0;

        void FlushColumn()
        {
            if (column == int.MinValue || current is null)
                return;
            current.Add(new Point(column, colMin));
            current.Add(new Point(column, colMax));
            current.Add(new Point(column, colLast));
        }

        for (var i = start; i <= end; i++)
        {
            var point = points[i];
            var px = x(point.Time < from ? from : point.Time);
            if (point.Value is not { } value)
            {
                if (decimate)
                    FlushColumn();
                if (current is not null && series.Step && lastY is { } held)
                    current.Add(new Point(px, held));
                if (current is not null)
                    segments.Add(current);
                current = null;
                lastY = null;
                column = int.MinValue;
                continue;
            }
            var py = y(value);
            if (decimate)
            {
                var col = (int)Math.Round(px);
                if (current is null)
                {
                    current = [new Point(px, py)];
                    (column, colMin, colMax, colLast) = (col, py, py, py);
                }
                else if (col != column)
                {
                    FlushColumn();
                    (column, colMin, colMax, colLast) = (col, py, py, py);
                }
                else
                {
                    (colMin, colMax, colLast) = (Math.Min(colMin, py), Math.Max(colMax, py), py);
                }
                lastY = py;
                continue;
            }
            if (current is null)
                current = [new Point(px, py)];
            else if (series.Step && lastY is { } previous)
            {
                current.Add(new Point(px, previous));
                current.Add(new Point(px, py));
            }
            else
            {
                current.Add(new Point(px, py));
            }
            lastY = py;
        }
        if (decimate)
            FlushColumn();
        if (current is not null)
        {
            // Escalón: el último valor se mantiene hasta el final de la ventana.
            if (series.Step && lastY is { } last)
                current.Add(new Point(x(to), last));
            segments.Add(current);
        }
        return segments;
    }

    private static (double Min, double Max) ValueRange(ChartSeries[] series, DateTimeOffset from, DateTimeOffset to)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in series)
        {
            var start = Math.Max(0, s.LastIndexAtOrBefore(from));
            for (var i = start; i < s.Points.Count && s.Points[i].Time <= to; i++)
            {
                if (s.Points[i].Value is not { } v)
                    continue;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }
        return min > max ? (0, 1) : (min, max);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static FormattedText Text(string text, IBrush brush, double size, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);
}

/// <summary>Tramo de tiempo seleccionado con el ratón.</summary>
public sealed class ChartZoomEventArgs(DateTimeOffset from, DateTimeOffset to) : EventArgs
{
    public DateTimeOffset From { get; } = from;

    public DateTimeOffset To { get; } = to;
}
