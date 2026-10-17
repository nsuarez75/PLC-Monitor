using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.Controls;

/// <summary>
/// Indicadores de panel: una tarjeta por tag con su valor actual en grande y, según el estilo, una aguja sobre un arco o
/// una barra de nivel. La escala es la fijada a mano o la de los valores de la ventana; fuera de los límites bajo/alto,
/// en rojo. Los booleanos son una luz.
/// </summary>
public sealed class GaugePanel : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<GaugePanel, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<DateTimeOffset?> FromProperty = AvaloniaProperty.Register<GaugePanel, DateTimeOffset?>(nameof(From));

    public static readonly StyledProperty<DateTimeOffset?> ToProperty = AvaloniaProperty.Register<GaugePanel, DateTimeOffset?>(nameof(To));

    /// <summary>Escala fijada a mano; null = la de los valores de la ventana.</summary>
    public static readonly StyledProperty<double?> ScaleMinProperty = AvaloniaProperty.Register<GaugePanel, double?>(nameof(ScaleMin));

    public static readonly StyledProperty<double?> ScaleMaxProperty = AvaloniaProperty.Register<GaugePanel, double?>(nameof(ScaleMax));

    public static readonly StyledProperty<double?> LowLimitProperty = AvaloniaProperty.Register<GaugePanel, double?>(nameof(LowLimit));

    public static readonly StyledProperty<double?> HighLimitProperty = AvaloniaProperty.Register<GaugePanel, double?>(nameof(HighLimit));

    public static readonly StyledProperty<GaugeStyle> StyleKindProperty = AvaloniaProperty.Register<GaugePanel, GaugeStyle>(nameof(StyleKind), GaugeStyle.Needle);

    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<GaugePanel, int>(nameof(Revision));

    public static readonly StyledProperty<string> EmptyTextProperty = AvaloniaProperty.Register<GaugePanel, string>(nameof(EmptyText), "Sin datos");

    private const double Gap = 12;
    private const double MinTileWidth = 230;
    private static readonly Typeface Font = new(FontFamily.Default);

    static GaugePanel()
    {
        AffectsRender<GaugePanel>(SeriesProperty, FromProperty, ToProperty, ScaleMinProperty, ScaleMaxProperty, LowLimitProperty, HighLimitProperty,
            StyleKindProperty, RevisionProperty, EmptyTextProperty);
    }

    public GaugePanel()
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

    public double? ScaleMin
    {
        get => GetValue(ScaleMinProperty);
        set => SetValue(ScaleMinProperty, value);
    }

    public double? ScaleMax
    {
        get => GetValue(ScaleMaxProperty);
        set => SetValue(ScaleMaxProperty, value);
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

    /// <summary>Estilo de los indicadores (no se llama Style para no chocar con los estilos de Avalonia).</summary>
    public GaugeStyle StyleKind
    {
        get => GetValue(StyleKindProperty);
        set => SetValue(StyleKindProperty, value);
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

    /// <summary>¿Está el valor fuera de los límites bajo/alto?</summary>
    public static bool OutOfLimits(double value, double? low, double? high) => value < (low ?? double.NegativeInfinity) || value > (high ?? double.PositiveInfinity);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 40 || bounds.Height < 40)
            return;
        var series = Series ?? [];
        if (series.Count == 0)
        {
            var empty = Text(EmptyText, Brush("App.TextMuted", Brushes.Gray), 13);
            context.DrawText(empty, new Point((bounds.Width - empty.Width) / 2, (bounds.Height - empty.Height) / 2));
            return;
        }
        var columns = Math.Clamp((int)((bounds.Width + Gap) / (MinTileWidth + Gap)), 1, series.Count);
        var rows = (int)Math.Ceiling(series.Count / (double)columns);
        var width = (bounds.Width - (Gap * (columns - 1))) / columns;
        var height = Math.Min(260, (bounds.Height - (Gap * (rows - 1))) / rows);
        for (var i = 0; i < series.Count; i++)
        {
            var tile = new Rect((i % columns) * (width + Gap), (i / columns) * (height + Gap), width, height);
            DrawTile(context, series[i], tile);
        }
    }

    private void DrawTile(DrawingContext context, ChartSeries series, Rect tile)
    {
        var text = Brush("App.Text", Brushes.Black);
        var muted = Brush("App.TextMuted", Brushes.Gray);
        var border = Brush("App.Border", Brushes.LightGray);
        var bad = Brush("App.Bad", Brushes.Red);
        var color = Brush($"App.Chart{series.ColorIndex % TrendChart.PaletteSize}", Brushes.SteelBlue);
        context.DrawRectangle(Brush("App.SurfaceAlt", Brushes.WhiteSmoke), new Pen(border, 1), tile, 8, 8);

        var name = Text(series.Name, muted, 12, FontWeight.SemiBold);
        name.MaxTextWidth = tile.Width - 24;
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(name, new Point(tile.X + 12, tile.Y + 10));
        var value = series.Points.Count > 0 ? series.Points[^1].Value : null;
        var body = new Rect(tile.X + 12, tile.Y + 34, tile.Width - 24, tile.Height - 46);

        // Booleano: una luz con su texto.
        if (series.IsBoolean)
        {
            var on = value is >= 0.5;
            var radius = Math.Min(body.Width, body.Height - 30) / 2 * 0.6;
            var center = new Point(body.Center.X, body.Y + radius + 6);
            context.DrawEllipse(value is null ? border : on ? color : Brush("App.Neutral", Brushes.Gray), new Pen(border, 2), center, radius, radius);
            var label = Text(series.Format(value), text, 22, FontWeight.SemiBold);
            context.DrawText(label, new Point(body.Center.X - (label.Width / 2), center.Y + radius + 6));
            return;
        }

        // Escala: la fijada o la de la ventana (incluidos los límites).
        var from = From ?? DateTimeOffset.MinValue;
        var to = To ?? DateTimeOffset.MaxValue;
        var values = Histogram.Values(series, from, to);
        if (value is { } current)
            values.Add(current);
        if (LowLimit is { } lo)
            values.Add(lo);
        if (HighLimit is { } hi)
            values.Add(hi);
        var (min, max, _) = values.Count == 0 ? (0, 1, 1) : ChartAxis.NiceRange(values.Min(), values.Max(), 4);
        if (ScaleMin is { } sMin)
            min = sMin;
        if (ScaleMax is { } sMax)
            max = sMax;
        if (max <= min)
            max = min + 1;
        double Fraction(double v) => Math.Clamp((v - min) / (max - min), 0, 1);

        var alarm = value is { } v0 && OutOfLimits(v0, LowLimit, HighLimit);
        var valueText = Text(series.Format(value), alarm ? bad : text, StyleKind == GaugeStyle.Value ? Math.Min(56, body.Height * 0.45) : 28, FontWeight.SemiBold);
        var track = new Pen(border, 10, lineCap: PenLineCap.Round);
        var limitPen = new Pen(bad, 3);
        switch (StyleKind)
        {
            case GaugeStyle.Needle:
            {
                // Arco de 180°: mínimo a la izquierda, máximo a la derecha.
                var radius = Math.Max(10, Math.Min(body.Width / 2, body.Height - valueText.Height - 18) - 8);
                var center = new Point(body.Center.X, body.Y + radius + 6);
                Point At(double fraction, double r) => new(center.X - (r * Math.Cos(Math.PI * fraction)), center.Y - (r * Math.Sin(Math.PI * fraction)));
                DrawArc(context, track, center, radius, 0, 1, At);
                if (value is { } v)
                    DrawArc(context, new Pen(alarm ? bad : color, 10, lineCap: PenLineCap.Round), center, radius, 0, Fraction(v), At);
                foreach (var limit in new[] { LowLimit, HighLimit })
                {
                    if (limit is { } l && l >= min && l <= max)
                        context.DrawLine(limitPen, At(Fraction(l), radius - 9), At(Fraction(l), radius + 9));
                }
                if (value is { } needle)
                {
                    context.DrawLine(new Pen(text, 2.5, lineCap: PenLineCap.Round), center, At(Fraction(needle), radius - 14));
                    context.DrawEllipse(text, null, center, 5, 5);
                }
                var minText = Text(ChartAxis.FormatValue(min), muted, 10.5);
                var maxText = Text(ChartAxis.FormatValue(max), muted, 10.5);
                context.DrawText(minText, new Point(center.X - radius - (minText.Width / 2), center.Y + 8));
                context.DrawText(maxText, new Point(center.X + radius - (maxText.Width / 2), center.Y + 8));
                context.DrawText(valueText, new Point(center.X - (valueText.Width / 2), center.Y + 8));
                break;
            }
            case GaugeStyle.Level:
            {
                // Barra vertical a la izquierda; el valor a su derecha.
                var bar = new Rect(body.X + 8, body.Y + 4, Math.Min(46, body.Width / 4), body.Height - 8);
                context.DrawRectangle(Brush("App.Surface", Brushes.White), new Pen(border, 1), bar, 4, 4);
                if (value is { } v)
                {
                    var h = bar.Height * Fraction(v);
                    context.DrawRectangle(alarm ? bad : color, null, new Rect(bar.X, bar.Bottom - h, bar.Width, h), 4, 4);
                }
                foreach (var limit in new[] { LowLimit, HighLimit })
                {
                    if (limit is { } l && l >= min && l <= max)
                    {
                        var y = bar.Bottom - (bar.Height * Fraction(l));
                        context.DrawLine(limitPen, new Point(bar.X - 5, y), new Point(bar.Right + 5, y));
                    }
                }
                var maxText = Text(ChartAxis.FormatValue(max), muted, 10.5);
                var minText = Text(ChartAxis.FormatValue(min), muted, 10.5);
                context.DrawText(maxText, new Point(bar.Right + 8, bar.Y - 2));
                context.DrawText(minText, new Point(bar.Right + 8, bar.Bottom - minText.Height + 2));
                context.DrawText(valueText, new Point(bar.Right + 8 + ((body.Right - bar.Right - 8 - valueText.Width) / 2), bar.Center.Y - (valueText.Height / 2)));
                break;
            }
            default:
                context.DrawText(valueText, new Point(body.Center.X - (valueText.Width / 2), body.Center.Y - (valueText.Height / 2)));
                break;
        }
        if (alarm)
        {
            var warn = Text(value < (LowLimit ?? double.NegativeInfinity) ? "Bajo el límite" : "Sobre el límite", bad, 11, FontWeight.SemiBold);
            context.DrawText(warn, new Point(tile.Right - warn.Width - 12, tile.Y + 10));
        }
    }

    private static void DrawArc(DrawingContext context, Pen pen, Point center, double radius, double from, double to, Func<double, double, Point> at)
    {
        if (to <= from)
            return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(at(from, radius), false);
            g.ArcTo(at(to, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static FormattedText Text(string text, IBrush brush, double size, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);
}
