using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PlcMonitor.App.Controls;

/// <summary>Muestra de color de una serie (App.Chart&lt;n&gt;), que sigue al tema claro u oscuro.</summary>
public sealed class ChartSwatch : Control
{
    public static readonly StyledProperty<int> ColorIndexProperty = AvaloniaProperty.Register<ChartSwatch, int>(nameof(ColorIndex));

    static ChartSwatch()
    {
        AffectsRender<ChartSwatch>(ColorIndexProperty);
    }

    public ChartSwatch()
    {
        Width = 14;
        Height = 4;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public int ColorIndex
    {
        get => GetValue(ColorIndexProperty);
        set => SetValue(ColorIndexProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var brush = this.TryFindResource($"App.Chart{ColorIndex % TrendChart.PaletteSize}", ActualThemeVariant, out var value) && value is IBrush b
            ? b
            : Brushes.SteelBlue;
        context.DrawRectangle(brush, null, new Rect(Bounds.Size), 2, 2);
    }
}
