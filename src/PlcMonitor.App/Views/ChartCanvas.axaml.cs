using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Controls;

namespace PlcMonitor.App.Views;

public sealed partial class ChartCanvas : UserControl
{
    public ChartCanvas()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TrendChart>("Chart")!.ZoomRequested += (_, e) => (DataContext as ChartViewModel)?.Zoom(e.From, e.To);
        this.FindControl<StateChart>("States")!.ZoomRequested += (_, e) => (DataContext as ChartViewModel)?.Zoom(e.From, e.To);
    }
}
