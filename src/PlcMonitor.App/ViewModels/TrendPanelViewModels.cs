using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.ViewModels;

/// <summary>Panel en la lista de la pestaña Tendencias.</summary>
public sealed partial class TrendPanelItem(TrendPanel panel) : ObservableObject
{
    public TrendPanel Panel { get; } = panel;

    [ObservableProperty]
    public partial string Name { get; set; } = panel.Name;

    [ObservableProperty]
    public partial string Description { get; set; } = Describe(panel);

    public void Refresh()
    {
        Name = Panel.Name;
        Description = Describe(Panel);
    }

    private static string Describe(TrendPanel panel)
    {
        var views = panel.Views.Count switch { 0 => "sin vistas", 1 => "1 vista", var n => $"{n} vistas" };
        return $"{views} · {panel.Columns} {(panel.Columns == 1 ? "columna" : "columnas")}";
    }
}

/// <summary>Celda de un panel abierto: la gráfica de una vista con su nombre, o por qué no se ha podido abrir.</summary>
public sealed class PanelCellViewModel(string title, OpenedView opened)
{
    public string Title { get; } = title;

    public LiveTrendViewModel? Live { get; } = opened.Live;

    public PeriodBarsViewModel? Bars { get; } = opened.Bars;

    public ChartViewModel? Chart => Live?.Chart ?? Bars?.Chart;

    public bool HasChart => Chart is not null;

    public string Error { get; } = opened.Error;

    public bool HasError => Error.Length > 0;

    /// <summary>Lo que dice la gráfica de sus datos (histórico, CSV o barras).</summary>
    public string Info => Live is { IsLive: false } live ? live.HistoryText : Bars?.Description ?? "";
}

/// <summary>
/// Panel abierto: las gráficas de sus vistas en una rejilla. Con <see cref="SyncZoom"/>, ampliar un tramo en una gráfica
/// de tiempo (tendencia o cronograma) lo amplía en las demás, y «Ajustar» las devuelve a todas.
/// </summary>
public sealed partial class OpenPanelViewModel : ObservableObject, IDisposable
{
    private bool _syncing;

    public OpenPanelViewModel(TrendPanel panel, IReadOnlyList<PanelCellViewModel> cells)
    {
        ArgumentNullException.ThrowIfNull(panel);
        Name = panel.Name;
        Columns = Math.Clamp(panel.Columns, 1, TrendPanel.MaxColumns);
        Cells = new ObservableCollection<PanelCellViewModel>(cells);
        SyncZoom = panel.SyncZoom;
        foreach (var chart in TimeCharts)
            chart.ZoomChanged += OnZoomChanged;
    }

    public string Name { get; }

    public int Columns { get; }

    public ObservableCollection<PanelCellViewModel> Cells { get; }

    [ObservableProperty]
    public partial bool SyncZoom { get; set; }

    /// <summary>Gráficas con eje de tiempo, las que comparten el zoom.</summary>
    private IEnumerable<ChartViewModel> TimeCharts => Cells.Select(c => c.Chart).OfType<ChartViewModel>().Where(c => c.Kind is ChartKind.Trend or ChartKind.States);

    private void OnZoomChanged(object? sender, EventArgs e)
    {
        if (_syncing || !SyncZoom || sender is not ChartViewModel source)
            return;
        _syncing = true;
        try
        {
            foreach (var chart in TimeCharts.Where(c => c != source))
            {
                if (source.IsZoomed && source.From is { } from && source.To is { } to)
                    chart.Zoom(from, to);
                else if (chart.IsZoomed)
                    chart.ResetZoomCommand.Execute(null);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Avanza las gráficas en vivo (temporizador de la pestaña).</summary>
    public void Refresh(DateTimeOffset now)
    {
        foreach (var cell in Cells)
            cell.Live?.Refresh(now);
    }

    public void RefreshTags()
    {
        foreach (var cell in Cells)
            cell.Live?.RefreshTags();
    }

    public void Dispose()
    {
        foreach (var chart in TimeCharts)
            chart.ZoomChanged -= OnZoomChanged;
        foreach (var cell in Cells)
            cell.Live?.Dispose();
    }
}
