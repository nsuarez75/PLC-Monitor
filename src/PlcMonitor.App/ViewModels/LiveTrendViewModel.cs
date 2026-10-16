using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Tendencia en vivo del dispositivo seleccionado: los tags que se marquen, con los valores que llegan del agente,
/// en una ventana que avanza con el reloj. Se pausa al hacer zoom o con el botón; «Ajustar» quita el zoom y sigue.
/// </summary>
public sealed partial class LiveTrendViewModel : ObservableObject
{
    private readonly Func<DeviceViewModel?> _device;

    public LiveTrendViewModel(Func<DeviceViewModel?> device)
    {
        _device = device;
        Chart.ZoomChanged += (_, _) => Paused = Chart.IsZoomed;
    }

    public static IReadOnlyList<Choice<TimeSpan>> Windows { get; } =
    [
        new("1 min", TimeSpan.FromMinutes(1)),
        new("5 min", TimeSpan.FromMinutes(5)),
        new("15 min", TimeSpan.FromMinutes(15)),
        new("1 h", TimeSpan.FromHours(1)),
    ];

    public ChartViewModel Chart { get; } = new() { EmptyText = "Marca uno o más tags para ver su tendencia" };

    [ObservableProperty]
    public partial Choice<TimeSpan> Window { get; set; } = Windows[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    public partial bool Paused { get; set; }

    public string PauseText => Paused ? "Seguir" : "Pausa";

    /// <summary>Nuevo dispositivo seleccionado.</summary>
    public void Reset() => RefreshTags(selectFirst: 1);

    /// <summary>Rehace la lista de tags (conserva lo marcado) a partir del dispositivo seleccionado.</summary>
    public void RefreshTags() => RefreshTags(selectFirst: 1);

    private void RefreshTags(int selectFirst)
    {
        var tags = _device()?.Tags.Where(t => t.IsChartable).ToArray() ?? [];
        var now = DateTimeOffset.UtcNow;
        Chart.SetData(tags.Select(t => new ChartSeriesData(t.Name, t.TrendPoints, Step: true, t.IsBoolean)), now - Window.Value, now, selectFirst);
    }

    /// <summary>Avanza la ventana hasta <paramref name="now"/> y redibuja (lo llama un temporizador de la vista).</summary>
    public void Refresh(DateTimeOffset now)
    {
        if (!Paused)
            Chart.SetWindow(now - Window.Value, now);
        Chart.Touch();
    }

    partial void OnWindowChanged(Choice<TimeSpan> value) => Refresh(DateTimeOffset.UtcNow);

    [RelayCommand]
    private void TogglePause()
    {
        if (!Paused)
        {
            Paused = true;
            return;
        }
        // Seguir: fuera el zoom y la ventana vuelve a avanzar con el reloj.
        if (Chart.IsZoomed)
            Chart.ResetZoomCommand.Execute(null);
        Paused = false;
        Refresh(DateTimeOffset.UtcNow);
    }
}
