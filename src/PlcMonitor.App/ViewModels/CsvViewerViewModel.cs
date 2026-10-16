using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.ViewModels;

/// <summary>Visor sin conexión de un CSV exportado desde la pestaña Histórico.</summary>
public sealed partial class CsvViewerViewModel(IDialogs dialogs) : ObservableObject
{
    public ChartViewModel Chart { get; } = new() { EmptyText = "Marca uno o más tags" };

    [ObservableProperty]
    public partial string Title { get; private set; } = "Visor de histórico";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string Error { get; private set; } = "";

    /// <summary>Carga un fichero. Devuelve false (con el motivo en <see cref="Error"/>) si no se puede leer.</summary>
    public bool Load(string path)
    {
        try
        {
            var content = HistoryCsv.Load(path);
            Chart.SetData(content.Series, content.From, content.To, selectFirst: 4);
            Title = Path.GetFileName(path);
            Error = "";
            var range = content.From is { } from && content.To is { } to
                ? $" · {from.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)} – {to.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)}"
                : "";
            Summary = $"{content.Series.Count} tags · {content.Rows} puntos" +
                      (content.Devices.Count > 0 ? $" · {string.Join(", ", content.Devices)}" : "") + range +
                      (content.SkippedRows > 0 ? $" · {content.SkippedRows} líneas no válidas" : "");
            if (content.Series.Count == 0)
                Error = "El fichero no tiene valores numéricos ni booleanos que dibujar.";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>Abre otro CSV en este mismo visor.</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await dialogs.OpenFileAsync("Abrir histórico (CSV)", "csv") is { } path)
            Load(path);
    }
}
