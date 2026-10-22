using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

public sealed partial class OpcUaView : UserControl
{
    public OpcUaView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
