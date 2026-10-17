using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.Controls;

/// <summary>
/// Barras agrupadas por periodo: cada punto de una serie es el valor de un periodo (su <see cref="ChartPoint.Time"/> es
/// el inicio), y los tags del mismo periodo van juntos. Al pasar el ratón, el periodo y el valor de cada tag.
/// </summary>
public sealed class BarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<BarPeriod> PeriodProperty = AvaloniaProperty.Register<BarChart, BarPeriod>(nameof(Period));

    public static readonly StyledProperty<int> ShiftStartHourProperty = AvaloniaProperty.Register<BarChart, int>(nameof(ShiftStartHour), 6);

    public static readonly StyledProperty<double?> YMinProperty = AvaloniaProperty.Register<BarChart, double?>(nameof(YMin));

    public static readonly StyledProperty<double?> YMaxProperty = AvaloniaProperty.Register<BarChart, double?>(nameof(YMax));

    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<BarChart, int>(nameof(Revision));

    public static readonly StyledProperty<string> EmptyTextProperty = AvaloniaProperty.Register<BarChart, string>(nameof(EmptyText), "Sin datos");

    private const double LegendHeight = 26;
    private const double AxisFontSize = 11;
    private static readonly Typeface Font = new(FontFamily.Default);
    private Point? _hover;

    static BarChart()
    {
        AffectsRender<BarChart>(SeriesProperty, PeriodProperty, ShiftStartHourProperty, YMinProperty, YMaxProperty, RevisionProperty, EmptyTextProperty);
    }

    public BarChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ClipToBounds = true;
    }

    public IReadOnlyList<ChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public BarPeriod Period
    {
        get => GetValue(PeriodProperty);
        set => SetValue(PeriodProperty, value);
    }

    public int ShiftStartHour
    {
        get => GetValue(ShiftStartHourProperty);
        set => SetValue(ShiftStartHourProperty, value);
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
        var series = (Series ?? []).Where(s => s.Points.Any(p => p.Value is not null)).ToArray();
        if (series.Length == 0)
        {
            var empty = Text(EmptyText, muted, 13);
            context.DrawText(empty, new Point((bounds.Width - empty.Width) / 2, (bounds.Height - empty.Height) / 2));
            return;
        }
        var periods = series.SelectMany(s => s.Points.Where(p => p.Value is not null).Select(p => p.Time)).Distinct().Order().ToArray();
        var values = series.Select(s => s.Points.Where(p => p.Value is not null).ToDictionary(p => p.Time, p => p.Value!.Value)).ToArray();
        var all = values.SelectMany(v => v.Values).ToArray();

        // Eje Y desde 0 (las barras salen de 0) salvo límites a mano.
        var ticks = Math.Max(2, (int)(bounds.Height / 55));
        var (yMin, yMax, yStep) = ChartAxis.NiceRange(YMin ?? Math.Min(0, all.Min()), YMax ?? Math.Max(0, all.Max()), ticks);
        if (YMin is { } fixedMin)
            yMin = fixedMin;
        if (YMax is { } fixedMax)
            yMax = fixedMax;
        if (yMax <= yMin)
            yMax = yMin + 1;
        var yTicks = new List<(double Value, FormattedText Label)>();
        for (var v = Math.Ceiling((yMin - (yStep / 1000)) / yStep) * yStep; v <= yMax + (yStep / 1000); v += yStep)
            yTicks.Add((v, Text(ChartAxis.FormatValue(Math.Round(v / yStep) * yStep), muted, AxisFontSize)));
        var left = yTicks.Max(t => t.Label.Width) + 12;
        var top = LegendHeight + 6;
        var plot = new Rect(left, top, Math.Max(10, bounds.Width - left - 12), Math.Max(10, bounds.Height - top - 24));
        double Y(double v) => plot.Bottom - (plot.Height * (Math.Clamp(v, yMin, yMax) - yMin) / (yMax - yMin));
        foreach (var (value, label) in yTicks)
        {
            context.DrawLine(grid, new Point(plot.Left, Y(value)), new Point(plot.Right, Y(value)));
            context.DrawText(label, new Point(left - 8 - label.Width, Y(value) - (label.Height / 2)));
        }

        // Un grupo por periodo; las etiquetas, saltando las que no caben.
        var group = plot.Width / periods.Length;
        var barWidth = Math.Max(1, group * 0.78 / series.Length);
        var labels = periods.Select(p => Text(PeriodBars.Label(p, Period, ShiftStartHour), muted, AxisFontSize)).ToArray();
        var every = Math.Max(1, (int)Math.Ceiling((labels.Max(l => l.Width) + 12) / group));
        var zero = Y(Math.Clamp(0, yMin, yMax));
        int? hovered = null;
        for (var i = 0; i < periods.Length; i++)
        {
            var gx = plot.Left + (i * group);
            if (_hover is { } h && h.X >= gx && h.X < gx + group && h.Y >= plot.Top && h.Y <= plot.Bottom)
            {
                hovered = i;
                context.DrawRectangle(new SolidColorBrush(Colors.Gray, 0.12), null, new Rect(gx, plot.Top, group, plot.Height));
            }
            for (var s = 0; s < series.Length; s++)
            {
                if (!values[s].TryGetValue(periods[i], out var v))
                    continue;
                var x = gx + (group * 0.11) + (s * barWidth);
                var y = Y(v);
                context.DrawRectangle(Brush($"App.Chart{series[s].ColorIndex % TrendChart.PaletteSize}", Brushes.SteelBlue), null,
                    new Rect(new Point(x, Math.Min(y, zero)), new Point(x + Math.Max(1, barWidth - 1), Math.Max(y, zero))), 2, 2);
            }
            if (i % every == 0)
                context.DrawText(labels[i], new Point(Math.Clamp(gx + (group / 2) - (labels[i].Width / 2), plot.Left - 4, bounds.Width - labels[i].Width - 2), plot.Bottom + 5));
        }

        // Leyenda: valor de cada tag en el periodo bajo el cursor (o en el último).
        var at = periods[hovered ?? periods.Length - 1];
        double lx = 4;
        if (hovered is not null)
        {
            var period = Text(PeriodBars.Label(at, Period, ShiftStartHour), text, 12, FontWeight.SemiBold);
            context.DrawText(period, new Point(lx, 3));
            lx += period.Width + 16;
        }
        for (var s = 0; s < series.Length; s++)
        {
            var name = Text(series[s].Name, muted, 12);
            var value = Text(values[s].TryGetValue(at, out var v) ? series[s].Format(v) : "—", text, 12, FontWeight.SemiBold);
            var itemWidth = 14 + 6 + name.Width + 6 + value.Width + 18;
            if (lx + itemWidth > bounds.Width && lx > 4)
            {
                context.DrawText(Text("…", muted, 12), new Point(lx, 4));
                break;
            }
            context.DrawRectangle(Brush($"App.Chart{series[s].ColorIndex % TrendChart.PaletteSize}", Brushes.SteelBlue), null, new Rect(lx, 10, 14, 3), 1.5, 1.5);
            context.DrawText(name, new Point(lx + 20, 3));
            context.DrawText(value, new Point(lx + 26 + name.Width, 3));
            lx += itemWidth;
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static FormattedText Text(string text, IBrush brush, double size, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);
}
