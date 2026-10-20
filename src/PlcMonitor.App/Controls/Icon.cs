using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PlcMonitor.App.Controls;

/// <summary>
/// Icono de línea (trazos de Lucide, licencia ISC, en una rejilla de 24×24). Se dibuja con el color de texto heredado,
/// así sigue al tema y al estado del botón que lo contiene.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    /// <summary>Color del trazo. Se hereda del texto del contenedor (botón, tarjeta...).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 2);

    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plus"] = "M5 12h14 M12 5v14",
        ["search"] = "M11 3a8 8 0 1 0 0 16a8 8 0 1 0 0-16z M21 21l-4.3-4.3",
        ["pencil"] = "M21.2 6.8a2.8 2.8 0 0 0-4-4L3.8 16.2 3 21l4.8-.8z M15 5l4 4",
        ["trash"] = "M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2",
        ["power"] = "M12 2v10 M18.4 6.6a9 9 0 1 1-12.8 0",
        ["sun"] = "M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M6.3 17.7l-1.4 1.4 M19.1 4.9l-1.4 1.4",
        ["moon"] = "M12 3a6 6 0 0 0 9 9a9 9 0 1 1-9-9z",
        ["maximize"] = "M15 3h6v6 M9 21H3v-6 M21 3l-7 7 M3 21l7-7",
        ["chart"] = "M3 3v18h18 M7 15l4-4 3 3 6-7",
        ["folder"] = "M6 14l1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.55 6a2 2 0 0 1-1.94 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2",
        ["pause"] = "M6 4h4v16H6z M14 4h4v16h-4z",
        ["play"] = "M6 3l14 9-14 9z",
        ["monitor"] = "M4 3h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M8 21h8 M12 17v4",
        ["pulse"] = "M22 12h-4l-3 9L9 3l-3 9H2",
        ["x"] = "M18 6L6 18 M6 6l12 12",
        ["refresh"] = "M21 12a9 9 0 1 1-3-6.7L21 8 M21 3v5h-5",
        ["check"] = "M20 6L9 17l-5-5",
        ["link"] = "M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7 M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7",
        ["unlink"] = "M18.8 13.2l1.7-1.7a5 5 0 0 0-7-7l-1.7 1.7 M5.2 10.8l-1.7 1.7a5 5 0 0 0 7 7l1.7-1.7 M8 2v3 M2 8h3 M16 22v-3 M22 16h-3",
        ["server"] = "M4 3h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M4 13h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2z M6 7h.01 M6 17h.01",
        ["cpu"] = "M6 4h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M9 9h6v6H9z M9 1v3 M15 1v3 M9 20v3 M15 20v3 M20 9h3 M20 14h3 M1 9h3 M1 14h3",
        ["list"] = "M8 6h13 M8 12h13 M8 18h13 M3 6h.01 M3 12h.01 M3 18h.01",
        ["send"] = "M22 2L11 13 M22 2l-7 20-4-9-9-4z",
        ["alert"] = "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20z M12 8v4 M12 16h.01",
        ["info"] = "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20z M12 16v-4 M12 8h.01",
        ["save"] = "M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z M17 21v-8H7v8 M7 3v5h8",
        ["chevron-down"] = "M6 9l6 6 6-6",
        ["chevron-right"] = "M9 18l6-6-6-6",
        ["replace"] = "M16 3l4 4-4 4 M20 7H4 M8 21l-4-4 4-4 M4 17h16",
        ["history"] = "M3 12a9 9 0 1 0 3-6.7L3 8 M3 3v5h5 M12 7v5l4 2",
        ["download"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3",
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, StrokeWidthProperty, ForegroundProperty);
        AffectsMeasure<Icon>(KindProperty);
    }

    public Icon()
    {
        Width = 16;
        Height = 16;
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public static bool IsKnown(string kind) => Paths.ContainsKey(kind);

    public override void Render(DrawingContext context)
    {
        if (Kind is not { } kind || !Paths.TryGetValue(kind, out var data))
            return;
        if (!Cache.TryGetValue(kind, out var geometry))
            Cache[kind] = geometry = Geometry.Parse(data);
        var brush = Foreground ?? Brushes.Gray;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        if (scale <= 0)
            return;
        // El grosor va en unidades de la rejilla de 24 (como en Lucide): se escala con el icono.
        var pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var offset = new Vector((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
            context.DrawGeometry(null, pen, geometry);
    }
}
