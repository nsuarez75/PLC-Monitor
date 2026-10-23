using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Localization;
using PlcMonitor.App.ViewModels;

namespace PlcMonitor.App.Views;

public sealed partial class ChartPanel : UserControl
{
    public ChartPanel()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("SavePng")!.Click += async (_, _) => await SavePngAsync();
        this.FindControl<Button>("CopyPng")!.Click += async (_, _) => await CopyPngAsync();
        this.FindControl<Button>("CopyStats")!.Click += async (_, _) => await CopyStatsAsync();
        this.FindControl<Button>("SaveStats")!.Click += async (_, _) => await SaveStatsAsync();
    }

    private ChartViewModel? Chart => DataContext as ChartViewModel;

    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    /// <summary>
    /// Imagen de la gráfica con su cabecera, del tamaño con que se ve ahora (con un mínimo), en tema claro. Null si no
    /// hay gráfica.
    /// </summary>
    public RenderTargetBitmap? RenderImage()
    {
        if (Chart is not { } chart)
            return null;
        var canvas = this.FindControl<ChartCanvas>("Canvas")!;
        var size = new Size(Math.Max(900, canvas.Bounds.Width + 40), Math.Max(520, canvas.Bounds.Height + 120));
        return ChartImage.Render(this.FindControl<Canvas>("ExportHost")!, new ChartImage(chart), size);
    }

    private async Task SavePngAsync()
    {
        if (Chart is not { } chart || Main is not { } main)
            return;
        var path = await main.Dialogs.SaveFileAsync(Loc.T("Exportar gráfica"), ChartExport.FileName(chart.Title, DateTime.Now, "png"), "png");
        if (path is null)
            return;
        try
        {
            using var image = RenderImage();
            image?.Save(path, PngBitmapEncoderOptions.Default);
            main.Report(Loc.F("Gráfica guardada en {0}", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            main.Report(Loc.F("No se ha podido guardar la imagen: {0}", ex.Message), isError: true);
        }
    }

    private async Task CopyPngAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        using var image = RenderImage();
        if (image is null)
            return;
        await clipboard.SetBitmapAsync(image);
        Main?.Report(Loc.T("Imagen de la gráfica copiada al portapapeles"));
    }

    private async Task CopyStatsAsync()
    {
        if (Chart is not { } chart || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(SeriesStats.ToTable(chart.Stats));
        Main?.Report(Loc.T("Estadísticas copiadas al portapapeles"));
    }

    private async Task SaveStatsAsync()
    {
        if (Chart is not { From: { } from, To: { } to } chart || Main is not { } main)
            return;
        var path = await main.Dialogs.SaveFileAsync(Loc.T("Exportar estadísticas"), ChartExport.FileName($"{chart.Title} estadisticas".Trim(), DateTime.Now, "csv"), "csv");
        if (path is null)
            return;
        try
        {
            await File.WriteAllTextAsync(path, SeriesStats.ToCsv(chart.Stats, from, to));
            main.Report(Loc.F("Estadísticas guardadas en {0}", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            main.Report(Loc.F("No se han podido guardar las estadísticas: {0}", ex.Message), isError: true);
        }
    }
}
