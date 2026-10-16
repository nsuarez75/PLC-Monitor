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
