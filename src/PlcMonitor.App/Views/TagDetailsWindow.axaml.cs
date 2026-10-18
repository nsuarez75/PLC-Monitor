using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PlcMonitor.App.ViewModels;

namespace PlcMonitor.App.Views;

/// <summary>Detalle y configuración de un tag. Se cierra con «Cerrar» o al guardar.</summary>
public sealed partial class TagDetailsWindow : Window
{
    public TagDetailsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        var trend = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        trend.Tick += (_, _) => (DataContext as TagDetailsViewModel)?.RefreshTrend(DateTimeOffset.UtcNow);
        Opened += (_, _) =>
        {
            (DataContext as TagDetailsViewModel)?.RefreshTrend(DateTimeOffset.UtcNow);
            trend.Start();
        };
        Closed += (_, _) => trend.Stop();
    }

    /// <summary>
    /// Limita la altura al área útil de la pantalla del propietario (descontando la barra de título),
    /// para que la ventana no se salga en pantallas pequeñas o con escalado; el contenido se desplaza.
    /// </summary>
    public void FitTo(Window owner)
    {
        if ((owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary) is not { } screen)
            return;
        var available = screen.WorkingArea.Height / screen.Scaling - TitleBarAllowance;
        if (available > 0 && available < MaxHeight)
            MaxHeight = available;
    }

    private const double TitleBarAllowance = 64;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is TagDetailsViewModel details)
            details.PropertyChanged += OnDetailsChanged;
    }

    private void OnDetailsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagDetailsViewModel.Saved) && sender is TagDetailsViewModel { Saved: true })
            Close();
    }
}
