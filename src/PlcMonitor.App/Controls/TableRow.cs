using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace PlcMonitor.App.Controls;

/// <summary>
/// Fila de una tabla (cabecera o fila de datos) con columnas fijas y proporcionales, como un <see cref="Grid"/> de una
/// sola fila, pero que se adapta al ancho: si a las proporcionales no les llega <see cref="MinStarWidth"/> por unidad,
/// oculta columnas en el orden de <see cref="HideOrder"/> hasta que quepan. Cabecera y filas con los mismos
/// parámetros ocultan lo mismo. Cada hijo va en su columna con <c>Grid.Column</c>.
/// </summary>
public sealed class TableRow : Panel
{
    public static readonly StyledProperty<string> ColumnsProperty =
        AvaloniaProperty.Register<TableRow, string>(nameof(Columns), "*");

    public static readonly StyledProperty<string> HideOrderProperty =
        AvaloniaProperty.Register<TableRow, string>(nameof(HideOrder), "");

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<TableRow, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> MinStarWidthProperty =
        AvaloniaProperty.Register<TableRow, double>(nameof(MinStarWidth), 60);

    private (string Columns, string HideOrder, double[] Values, bool[] Stars, int[] Order)? _parsed;

    static TableRow()
    {
        AffectsMeasure<TableRow>(ColumnsProperty, HideOrderProperty, ColumnSpacingProperty, MinStarWidthProperty);
    }

    /// <summary>Anchos separados por comas: número (fijo) o <c>n*</c> (proporcional).</summary>
    public string Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>Índices de las columnas que se pueden ocultar, en orden (la primera, la primera en irse).</summary>
    public string HideOrder
    {
        get => GetValue(HideOrderProperty);
        set => SetValue(HideOrderProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>Ancho mínimo por unidad proporcional antes de ocultar columnas.</summary>
    public double MinStarWidth
    {
        get => GetValue(MinStarWidthProperty);
        set => SetValue(MinStarWidthProperty, value);
    }

    /// <summary>Ancho de cada columna para un ancho total; 0 = oculta.</summary>
    public double[] Layout(double width)
    {
        var (values, stars, order) = Parse();
        var visible = Enumerable.Repeat(true, values.Length).ToArray();
        for (var hidden = 0; ; hidden++)
        {
            var count = visible.Count(v => v);
            var fixedWidth = values.Where((_, i) => visible[i] && !stars[i]).Sum() + (ColumnSpacing * Math.Max(0, count - 1));
            var starUnits = values.Where((_, i) => visible[i] && stars[i]).Sum();
            var perStar = starUnits > 0 ? (width - fixedWidth) / starUnits : 0;
            if (perStar >= MinStarWidth || hidden >= order.Length || double.IsInfinity(width))
            {
                if (double.IsInfinity(width))
                    perStar = MinStarWidth;
                return [.. values.Select((v, i) => !visible[i] ? 0 : stars[i] ? Math.Max(0, v * perStar) : v)];
            }
            visible[order[hidden]] = false;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var widths = Layout(availableSize.Width);
        var height = 0.0;
        foreach (var child in Children)
        {
            var width = ColumnWidth(widths, child);
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        var total = double.IsInfinity(availableSize.Width) ? Offset(widths, widths.Length) : availableSize.Width;
        return new Size(total, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = Layout(finalSize.Width);
        foreach (var child in Children)
        {
            var column = Math.Clamp(Grid.GetColumn(child), 0, widths.Length - 1);
            // Una columna oculta se coloca sin tamaño y no se pinta (un texto se saldría de ella).
            child.Opacity = widths[column] > 0 ? 1 : 0;
            child.Arrange(widths[column] > 0
                ? new Rect(Offset(widths, column), 0, widths[column], finalSize.Height)
                : default);
        }
        return finalSize;
    }

    private static double ColumnWidth(double[] widths, Control child) => widths[Math.Clamp(Grid.GetColumn(child), 0, widths.Length - 1)];

    private double Offset(double[] widths, int column)
    {
        var x = 0.0;
        for (var i = 0; i < column; i++)
        {
            if (widths[i] > 0)
                x += widths[i] + ColumnSpacing;
        }
        return x;
    }

    private (double[] Values, bool[] Stars, int[] Order) Parse()
    {
        if (_parsed is { } p && p.Columns == Columns && p.HideOrder == HideOrder)
            return (p.Values, p.Stars, p.Order);
        var parts = Columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        var stars = new bool[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            stars[i] = parts[i].EndsWith('*');
            var number = stars[i] ? parts[i][..^1] : parts[i];
            values[i] = number.Length == 0 ? 1 : double.Parse(number, CultureInfo.InvariantCulture);
        }
        var order = HideOrder.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).Where(i => i >= 0 && i < parts.Length).ToArray();
        _parsed = (Columns, HideOrder, values, stars, order);
        return (values, stars, order);
    }
}
