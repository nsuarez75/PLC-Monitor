using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

public sealed partial class AgentsView : UserControl
{
    public AgentsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
