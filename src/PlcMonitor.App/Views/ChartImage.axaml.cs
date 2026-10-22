using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.Views;

/// <summary>
/// Imagen de una gráfica para exportar: se monta fuera de la vista (en un contenedor de tamaño cero del árbol, para que
/// tenga estilos y recursos), siempre con el tema claro y fondo blanco, y se dibuja a un mapa de bits.
/// </summary>
public sealed partial class ChartImage : UserControl
{
    /// <summary>Escala de la imagen respecto a la pantalla (2 = el doble de píxeles, nítida al imprimir).</summary>
    public const double Scale = 2;

    public ChartImage()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ChartImage(ChartViewModel chart) : this()
    {
        ArgumentNullException.ThrowIfNull(chart);
        DataContext = chart;
        this.FindControl<TextBlock>("TitleText")!.Text = chart.Title.Length > 0 ? chart.Title : string.Join(", ", chart.Series.Select(s => s.Name));
        this.FindControl<TextBlock>("SubtitleText")!.Text = ChartExport.Subtitle(chart);
    }

    /// <summary>
    /// Dibuja <paramref name="content"/> de <paramref name="size"/> (en unidades de pantalla) con el tema claro, fondo
    /// blanco y un margen, colgándolo un momento de <paramref name="host"/> (un panel del árbol visual).
    /// </summary>
    public static RenderTargetBitmap Render(Panel host, Control content, Size size)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(content);
        var root = new ThemeVariantScope
        {
            RequestedThemeVariant = ThemeVariant.Light,
            Width = Math.Max(320, size.Width),
            Height = Math.Max(240, size.Height),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Border { Background = Brushes.White, Padding = new Thickness(20), Child = content },
        };
        // Se amplía con una transformación y se dibuja a 96 ppp: dibujar a más ppp descoloca los textos de las gráficas.
        var scaled = new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(Scale, Scale),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = root,
        };
        host.Children.Add(scaled);
        try
        {
            var pixels = new PixelSize((int)Math.Ceiling(root.Width * Scale), (int)Math.Ceiling(root.Height * Scale));
            scaled.Measure(new Size(pixels.Width, pixels.Height));
            scaled.Arrange(new Rect(0, 0, pixels.Width, pixels.Height));
            scaled.UpdateLayout();
            var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
            bitmap.Render(scaled);
            return bitmap;
        }
        finally
        {
            host.Children.Remove(scaled);
            scaled.Child = null;
            ((Border)root.Child!).Child = null;
        }
    }
}
