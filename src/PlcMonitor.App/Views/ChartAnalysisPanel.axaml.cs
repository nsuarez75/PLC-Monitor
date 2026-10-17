using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

public sealed partial class ChartAnalysisPanel : UserControl
{
    public ChartAnalysisPanel() => AvaloniaXamlLoader.Load(this);
}
