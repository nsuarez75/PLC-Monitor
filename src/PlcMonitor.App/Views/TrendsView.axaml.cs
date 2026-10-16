using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

public sealed partial class TrendsView : UserControl
{
    public TrendsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
