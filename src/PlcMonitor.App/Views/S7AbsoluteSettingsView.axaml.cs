using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

public sealed partial class S7AbsoluteSettingsView : UserControl
{
    public S7AbsoluteSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
