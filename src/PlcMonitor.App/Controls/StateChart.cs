using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Localization;

namespace PlcMonitor.App.Controls;

/// <summary>
/// Cronograma de estados: una franja por tag con un color por valor (booleanos: true con el color del tag, false
/// apagado) y, bajo el nombre, el porcentaje de tiempo en cada estado. Al pasar el ratón, el estado y su duración;
/// arrastrando, zoom en el tiempo como en <see cref="TrendChart"/>.
/// </summary>
public sealed class StateChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<StateChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<DateTimeOffset?> FromProperty = AvaloniaProperty.Register<StateChart, DateTimeOffset?>(nameof(From));

    public static readonly StyledProperty<DateTimeOffset?> ToProperty = AvaloniaProperty.Register<StateChart, DateTimeOffset?>(nameof(To));

    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<StateChart, int>(nameof(Revision));

    public static readonly StyledProperty<string> EmptyTextProperty = AvaloniaProperty.Register<StateChart, string>(nameof(EmptyText), "Sin datos");

    private const double AxisFontSize = 11;
    private const double MaxLaneHeight = 48;
    private const double LaneGap = 8;
    private static readonly Typeface Font = new(FontFamily.Default);
    private Point? _hover;
    private double? _dragStart;
    private Rect _plot;
    private (DateTimeOffset From, DateTimeOffset To)? _window;

    static StateChart()
    {
        AffectsRender<StateChart>(SeriesProperty, FromProperty, ToProperty, RevisionProperty, EmptyTextProperty);
    }

    public StateChart()
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

    public event EventHandler<ChartZoomEventArgs>? ZoomRequested;

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
        // Fondo transparente: el ratón llega a toda la gráfica, también a las zonas sin dibujo.
        context.FillRectangle(Brushes.Transparent, bounds);
        var text = Brush("App.Text", Brushes.Black);
        var muted = Brush("App.TextMuted", Brushes.Gray);
        var border = Brush("App.Border", Brushes.LightGray);
        var grid = new Pen(border, 1);
        var series = (Series ?? []).Where(s => s.Points.Any(p => p.Value is not null)).ToArray();
        _window = null;
        if (series.Length == 0)
        {
            var empty = Text(Loc.T(EmptyText), muted, 13);
            context.DrawText(empty, new Point((bounds.Width - empty.Width) / 2, (bounds.Height - empty.Height) / 2));
            return;
        }
        var from = From ?? series.Min(s => s.Points[0].Time);
        var to = To ?? series.Max(s => s.Points[^1].Time);
        if (to <= from)
            to = from + TimeSpan.FromSeconds(1);

        // Tramos y reparto de cada tag; los colores de los estados no booleanos van por orden de valor.
        var lanes = series.Select(s =>
        {
            var segments = StateTimeline.Segments(s, from, to);
            var values = segments.Where(g => g.Value is not null).Select(g => g.Value!.Value).Distinct().Order().ToList();
            return (Series: s, Segments: segments, Shares: StateTimeline.Shares(segments), Values: values);
        }).ToArray();

        var nameWidth = Math.Min(240, lanes.Max(l => Math.Max(Text(l.Series.Name, text, 12, FontWeight.SemiBold).Width, Text(Summary(l.Series, l.Shares), muted, 10.5).Width)));
        var left = nameWidth + 18;
        var plot = new Rect(left, 8, Math.Max(10, bounds.Width - left - 12), Math.Max(10, bounds.Height - 8 - 24));
        _plot = plot;
        _window = (from, to);
        var laneHeight = Math.Min(MaxLaneHeight, (plot.Height - (LaneGap * (lanes.Length - 1))) / lanes.Length);
        double X(DateTimeOffset t) => plot.Left + (plot.Width * (t - from).TotalMilliseconds / (to - from).TotalMilliseconds);

        // Marcas de tiempo.
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

        (string Lane, StateSegment Segment, string Value)? hovered = null;
        for (var i = 0; i < lanes.Length; i++)
        {
            var (s, segments, shares, values) = lanes[i];
            var top = plot.Top + (i * (laneHeight + LaneGap));
            var lane = new Rect(plot.Left, top, plot.Width, laneHeight);

            // Nombre y reparto.
            var name = Text(s.Name, text, 12, FontWeight.SemiBold);
            name.MaxTextWidth = nameWidth;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(name, new Point(4, top + (laneHeight >= 34 ? 2 : (laneHeight - name.Height) / 2)));
            if (laneHeight >= 34)
            {
                var summary = Text(Summary(s, shares), muted, 10.5);
                summary.MaxTextWidth = nameWidth;
                summary.MaxLineCount = 1;
                summary.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(summary, new Point(4, top + name.Height + 4));
            }

            context.DrawRectangle(null, grid, lane, 3, 3);
            using (context.PushClip(lane))
            {
                foreach (var segment in segments)
                {
                    if (segment.Value is not { } value)
                        continue;
                    var x1 = X(segment.From);
                    var x2 = Math.Max(x1 + 1, X(segment.To));
                    var rect = new Rect(x1, top, x2 - x1, laneHeight);
                    var fill = StateBrush(s, value, values, border);
                    context.DrawRectangle(fill, null, rect);
                    var label = Text(s.Format(value), IsOff(s, value) ? muted : Brushes.White, 11, FontWeight.SemiBold);
                    if (label.Width + 8 <= rect.Width && label.Height <= laneHeight)
                        context.DrawText(label, new Point(rect.X + 4, top + ((laneHeight - label.Height) / 2)));
                    if (_hover is { } h && rect.Contains(h))
                        hovered = (s.Name, segment, s.Format(value));
                }
            }
        }

        if (_dragStart is { } dragStart && _hover is { } dragNow)
        {
            var x1 = Math.Clamp(Math.Min(dragStart, dragNow.X), plot.Left, plot.Right);
            var x2 = Math.Clamp(Math.Max(dragStart, dragNow.X), plot.Left, plot.Right);
            context.DrawRectangle(new SolidColorBrush(Colors.Gray, 0.15), null, new Rect(x1, plot.Top, x2 - x1, plot.Height));
        }

        // Cursor: línea vertical y, sobre un tramo, su estado y duración.
        if (_hover is { } hover && plot.Contains(hover))
        {
            context.DrawLine(new Pen(muted, 1, dashStyle: DashStyle.Dash), new Point(hover.X, plot.Top), new Point(hover.X, plot.Bottom));
            if (hovered is { } info)
            {
                var format = span > TimeSpan.FromDays(1) ? "dd/MM HH:mm:ss" : "HH:mm:ss";
                var label = Text($"{info.Lane}: {info.Value} · desde {info.Segment.From.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)} · {StateTimeline.FormatDuration(info.Segment.Duration)}", text, AxisFontSize);
                var box = new Rect(Math.Clamp(hover.X + 10, 0, bounds.Width - label.Width - 12), Math.Clamp(hover.Y + 14, 0, bounds.Height - label.Height - 4),
                    label.Width + 12, label.Height + 4);
                context.DrawRectangle(Brush("App.Surface", Brushes.White), grid, box, 4, 4);
                context.DrawText(label, new Point(box.X + 6, box.Y + 2));
            }
        }
    }

    /// <summary>Reparto bajo el nombre: los tres estados con más tiempo.</summary>
    private static string Summary(ChartSeries series, List<StateShare> shares) =>
        string.Join(" · ", shares.Take(3).Select(sh => $"{series.Format(sh.Value)} {(sh.Fraction * 100).ToString("0", CultureInfo.InvariantCulture)}%"));

    /// <summary>Booleano a false (o 0): el estado «apagado», sin color.</summary>
    private static bool IsOff(ChartSeries series, double value) => series.IsBoolean && value < 0.5;

    private IBrush StateBrush(ChartSeries series, double value, List<double> values, IBrush off)
    {
        if (series.IsBoolean)
            return value >= 0.5 ? Brush($"App.Chart{series.ColorIndex % TrendChart.PaletteSize}", Brushes.SteelBlue) : off;
        var index = Math.Max(0, values.IndexOf(value));
        return Brush($"App.Chart{index % TrendChart.PaletteSize}", Brushes.SteelBlue);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static FormattedText Text(string text, IBrush brush, double size, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);
}
