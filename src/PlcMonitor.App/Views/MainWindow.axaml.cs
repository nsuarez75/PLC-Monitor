using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlcMonitor.App.ViewModels;

namespace PlcMonitor.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>Por debajo de este ancho los botones de la barra superior se quedan solo con el icono.</summary>
    private const double NarrowWidth = 1200;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ListBox>("TagsList")!.DoubleTapped += OnTagDoubleTapped;
        // La tendencia en vivo avanza con el reloj aunque no lleguen valores nuevos.
        var trend = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        trend.Tick += (_, _) => (DataContext as MainViewModel)?.Trends?.Refresh(DateTimeOffset.UtcNow);
        Opened += (_, _) => trend.Start();
        SizeChanged += (_, e) => Classes.Set("narrow", e.NewSize.Width < NarrowWidth);
        Closed += (_, _) => trend.Stop();
    }

    /// <summary>Doble clic en una fila de tags (fuera del campo y los botones de escritura): abre su detalle.</summary>
    private void OnTagDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is TagViewModel tag && DataContext is MainViewModel main)
            main.OpenTagDetailsCommand.Execute(tag);
    }
}
