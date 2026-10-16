using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Tendencia en vivo de unos tags (los de una vista de tendencia), con los valores que llegan del agente, en una
/// ventana que avanza con el reloj. Se pausa al hacer zoom o con el botón; «Seguir» quita el zoom y continúa.
/// </summary>
public sealed partial class LiveTrendViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<(string Name, TagViewModel? Tag)>> _tags;

    /// <param name="tags">Tags a dibujar con su nombre en la leyenda; null si el tag ya no existe en el agente.</param>
    public LiveTrendViewModel(Func<IReadOnlyList<(string Name, TagViewModel? Tag)>> tags)
    {
        _tags = tags;
        Chart.ZoomChanged += (_, _) => Paused = Chart.IsZoomed;
    }

    public static IReadOnlyList<Choice<TimeSpan>> Windows { get; } =
    [
        new("1 min", TimeSpan.FromMinutes(1)),
        new("5 min", TimeSpan.FromMinutes(5)),
        new("15 min", TimeSpan.FromMinutes(15)),
        new("1 h", TimeSpan.FromHours(1)),
    ];

    public ChartViewModel Chart { get; } = new() { EmptyText = "Aún no hay valores de estos tags" };

    [ObservableProperty]
    public partial Choice<TimeSpan> Window { get; set; } = Windows[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    public partial bool Paused { get; set; }

    public string PauseText => Paused ? "Seguir" : "Pausa";

    /// <summary>Tags de la vista que el agente ya no tiene (se han borrado o renombrado).</summary>
    [ObservableProperty]
    public partial string Missing { get; private set; } = "";

    /// <summary>Vuelve a enlazar las series con los tags (al abrir y cuando el agente reconfigura o se reconecta).</summary>
    public void RefreshTags()
    {
        var tags = _tags();
        var now = DateTimeOffset.UtcNow;
        Chart.SetData(
            tags.Where(t => t.Tag is not null).Select(t => new ChartSeriesData(t.Name, t.Tag!.TrendPoints, Step: true, t.Tag.IsBoolean)),
            now - Window.Value, now);
        var missing = tags.Where(t => t.Tag is null).Select(t => t.Name).ToArray();
        Missing = missing.Length == 0 ? "" : $"No disponibles en el agente: {string.Join(", ", missing)}";
        Refresh(now);
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
