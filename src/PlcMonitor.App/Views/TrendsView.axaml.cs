using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PlcMonitor.App.Charts;
using PlcMonitor.App.ViewModels;

namespace PlcMonitor.App.Views;

public sealed partial class TrendsView : UserControl
{
    /// <summary>Alto de cada gráfica de un panel (en pantalla y en la imagen).</summary>
    public const double CellHeight = 420;

    /// <summary>Ancho de cada columna del panel en la imagen exportada.</summary>
    private const double ImageColumnWidth = 720;

    public TrendsView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("SavePanelPng")!.Click += async (_, _) => await SavePanelPngAsync();
        this.FindControl<Button>("CopyPanelPng")!.Click += async (_, _) => await CopyPanelPngAsync();
    }

    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    /// <summary>
    /// Imagen del panel abierto: su nombre, el agente y la hora, y la gráfica de cada vista con su cabecera, en la misma
    /// rejilla. Null si no hay panel abierto.
    /// </summary>
    public RenderTargetBitmap? RenderPanelImage()
    {
        if (DataContext is not TrendsViewModel { OpenPanel: { } panel } || Main is not { } main)
            return null;
        var grid = new UniformGrid { Columns = panel.Columns };
        foreach (var cell in panel.Cells)
        {
            Control item = cell.Chart is { } chart
                ? new ChartImage(chart)
                : new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = cell.Title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = cell.Error, TextWrapping = TextWrapping.Wrap, Classes = { "error" } },
                    },
                };
            grid.Children.Add(new Border { Height = CellHeight, Margin = new Thickness(0, 0, 24, 24), Child = item });
        }
        var header = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(0, 0, 0, 16),
            Children =
            {
                new TextBlock { Text = panel.Name, FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"{main.AgentTitle} · {DateTime.Now:dd/MM/yyyy HH:mm:ss}", FontSize = 12, Classes = { "muted" } },
            },
        };
        var content = new DockPanel { Children = { header, grid } };
        DockPanel.SetDock(header, Dock.Top);
        var rows = (panel.Cells.Count + panel.Columns - 1) / panel.Columns;
        var size = new Size((panel.Columns * (ImageColumnWidth + 24)) + 40, (rows * (CellHeight + 24)) + 120);
        return ChartImage.Render(this.FindControl<Canvas>("ExportHost")!, content, size);
    }

    private async Task SavePanelPngAsync()
    {
        if (DataContext is not TrendsViewModel { OpenPanel: { } panel } || Main is not { } main)
            return;
        var path = await main.Dialogs.SaveFileAsync("Exportar panel", ChartExport.FileName(panel.Name, DateTime.Now, "png"), "png");
        if (path is null)
            return;
        try
        {
            using var image = RenderPanelImage();
            image?.Save(path, PngBitmapEncoderOptions.Default);
            main.Report($"Panel guardado en {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            main.Report($"No se ha podido guardar la imagen: {ex.Message}", isError: true);
        }
    }

    private async Task CopyPanelPngAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        using var image = RenderPanelImage();
        if (image is null)
            return;
        await clipboard.SetBitmapAsync(image);
        Main?.Report("Imagen del panel copiada al portapapeles");
    }
}
