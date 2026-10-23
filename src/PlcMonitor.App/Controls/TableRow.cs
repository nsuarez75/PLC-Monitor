using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace PlcMonitor.App.Controls;

/// <summary>
/// Fila de una tabla (cabecera o fila de datos) con columnas fijas y proporcionales, como un <see cref="Grid"/> de una
/// sola fila, pero que se adapta al ancho: si a las proporcionales no les llega <see cref="MinStarWidth"/> por unidad,
/// oculta columnas en el orden de <see cref="HideOrder"/> hasta que quepan. Las de <see cref="HiddenColumns"/> (las
/// que oculta el usuario) no se muestran nunca. Cabecera y filas con los mismos parámetros ocultan lo mismo. Cada hijo va en su columna con <c>Grid.Column</c>.
/// Con <see cref="CanResize"/> (la cabecera), arrastrar el borde entre dos columnas pasa ancho de una a la otra y
/// cambia <see cref="Columns"/>; las filas que lo enlazan siguen el cambio.
/// </summary>
public sealed class TableRow : Panel
{
    public static readonly StyledProperty<string> ColumnsProperty =
        AvaloniaProperty.Register<TableRow, string>(nameof(Columns), "*");

    public static readonly StyledProperty<string> HideOrderProperty =
        AvaloniaProperty.Register<TableRow, string>(nameof(HideOrder), "");

    public static readonly StyledProperty<string> HiddenColumnsProperty =
        AvaloniaProperty.Register<TableRow, string>(nameof(HiddenColumns), "");

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<TableRow, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> MinStarWidthProperty =
        AvaloniaProperty.Register<TableRow, double>(nameof(MinStarWidth), 60);

    public static readonly StyledProperty<bool> CanResizeProperty =
        AvaloniaProperty.Register<TableRow, bool>(nameof(CanResize));

    public static readonly StyledProperty<ICommand?> ResizeCompletedCommandProperty =
        AvaloniaProperty.Register<TableRow, ICommand?>(nameof(ResizeCompletedCommand));

    /// <summary>Ancho mínimo de una columna al arrastrar su borde.</summary>
    public const double MinResizeWidth = 32;

    // Distancia al borde, a cada lado, en la que se puede agarrar.
    private const double GripWidth = 5;

    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    private (string Columns, string HideOrder, string Hidden, double[] Values, bool[] Stars, int[] Order, bool[] Off)? _parsed;
    private Drag? _drag;

    // Arrastre en curso: columnas a cada lado del borde y valores de partida.
    private sealed record Drag(int Left, int Right, double StartX, double[] Values, double[] Widths, double PerStar);

    static TableRow()
    {
        AffectsMeasure<TableRow>(ColumnsProperty, HideOrderProperty, HiddenColumnsProperty, ColumnSpacingProperty, MinStarWidthProperty);
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

    /// <summary>Índices de las columnas que el usuario ha ocultado, separados por comas (no se muestran nunca).</summary>
    public string HiddenColumns
    {
        get => GetValue(HiddenColumnsProperty);
        set => SetValue(HiddenColumnsProperty, value);
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

    /// <summary>Permite cambiar el ancho de las columnas arrastrando sus bordes.</summary>
    public bool CanResize
    {
        get => GetValue(CanResizeProperty);
        set => SetValue(CanResizeProperty, value);
    }

    /// <summary>Se ejecuta al soltar el borde (para guardar <see cref="Columns"/>).</summary>
    public ICommand? ResizeCompletedCommand
    {
        get => GetValue(ResizeCompletedCommandProperty);
        set => SetValue(ResizeCompletedCommandProperty, value);
    }

    /// <summary>
    /// <see cref="Columns"/> tras mover <paramref name="delta"/> píxeles el borde derecho de la columna
    /// <paramref name="left"/> (lo gana o lo pierde la columna visible siguiente), con un ancho total de
    /// <paramref name="width"/>. El resto de columnas no cambia. null si no hay borde ahí.
    /// </summary>
    public string? ResizedColumns(double width, int left, double delta)
    {
        var (values, stars, _) = Parse();
        var widths = Layout(width);
        var right = NextVisible(widths, left);
        if (left < 0 || left >= widths.Length || widths[left] <= 0 || right < 0)
            return null;
        return Resized(new Drag(left, right, 0, values, widths, PerStar(values, stars, widths)), delta);
    }

    /// <summary>Ancho de cada columna para un ancho total; 0 = oculta.</summary>
    public double[] Layout(double width)
    {
        var (values, stars, order) = Parse();
        var off = _parsed!.Value.Off;
        var visible = off.Select(o => !o).ToArray();
        order = [.. order.Where(i => visible[i])];
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

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!CanResize)
            return;
        var x = e.GetPosition(this).X;
        if (_drag is { } drag)
        {
            if (Resized(drag, x - drag.StartX) is { } columns)
                SetCurrentValue(ColumnsProperty, columns);
            e.Handled = true;
            return;
        }
        Cursor = BorderAt(x) >= 0 ? ResizeCursor : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanResize || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var x = e.GetPosition(this).X;
        var left = BorderAt(x);
        if (left < 0)
            return;
        var (values, stars, _) = Parse();
        var widths = Layout(Bounds.Width);
        _drag = new Drag(left, NextVisible(widths, left), x, values, widths, PerStar(values, stars, widths));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null)
            return;
        _drag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (ResizeCompletedCommand is { } command && command.CanExecute(null))
            command.Execute(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = null;
    }

    // Columna a la izquierda del borde bajo x (solo entre dos columnas visibles), o -1.
    private int BorderAt(double x)
    {
        var widths = Layout(Bounds.Width);
        for (var i = 0; i < widths.Length; i++)
        {
            if (widths[i] <= 0 || NextVisible(widths, i) < 0)
                continue;
            var border = Offset(widths, i) + widths[i] + (ColumnSpacing / 2);
            if (Math.Abs(x - border) <= GripWidth + (ColumnSpacing / 2))
                return i;
        }
        return -1;
    }

    private static int NextVisible(double[] widths, int column)
    {
        for (var i = column + 1; i < widths.Length; i++)
        {
            if (widths[i] > 0)
                return i;
        }
        return -1;
    }

    // Píxeles por unidad proporcional con el reparto actual.
    private static double PerStar(double[] values, bool[] stars, double[] widths)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (stars[i] && widths[i] > 0 && values[i] > 0)
                return widths[i] / values[i];
        }
        return 0;
    }

    // Pasa delta píxeles entre la columna izquierda y las de su derecha: al ensanchar, se toma de la vecina y, cuando
    // llega al mínimo, de las siguientes; al estrechar, todo va a la vecina. Una proporcional cambia en unidades al
    // ritmo actual: así el resto de proporcionales conserva su ancho aunque la otra sea fija.
    private string? Resized(Drag drag, double delta)
    {
        var (_, stars, _) = Parse();
        if (drag.Values.Length != stars.Length || drag.PerStar <= 0 && (stars[drag.Left] || stars[drag.Right]))
            return null;
        var values = (double[])drag.Values.Clone();
        if (delta < 0)
        {
            delta = Math.Max(delta, MinResizeWidth - drag.Widths[drag.Left]);
            Shift(drag.Right, -delta);
        }
        else
        {
            var taken = 0.0;
            for (var i = drag.Right; i >= 0 && taken < delta; i = NextVisible(drag.Widths, i))
            {
                var take = Math.Min(delta - taken, Math.Max(0, drag.Widths[i] - MinResizeWidth));
                if (take > 0 && (!stars[i] || drag.PerStar > 0))
                {
                    Shift(i, -take);
                    taken += take;
                }
            }
            delta = taken;
        }
        if (delta == 0)
            return null;
        Shift(drag.Left, delta);
        return string.Join(",", values.Select((v, i) => Math.Round(v, stars[i] ? 4 : 1).ToString(CultureInfo.InvariantCulture) + (stars[i] ? "*" : "")));

        void Shift(int column, double pixels) => values[column] += stars[column] ? pixels / drag.PerStar : pixels;
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
        if (_parsed is { } p && p.Columns == Columns && p.HideOrder == HideOrder && p.Hidden == HiddenColumns)
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
        var order = Indices(HideOrder, parts.Length);
        var off = new bool[parts.Length];
        foreach (var i in Indices(HiddenColumns, parts.Length))
            off[i] = true;
        _parsed = (Columns, HideOrder, HiddenColumns, values, stars, order, off);
        return (values, stars, order);

        static int[] Indices(string list, int count) => list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1).Where(i => i >= 0 && i < count).ToArray();
    }
}
