using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

public sealed partial class S7SymbolicSettingsView : UserControl
{
    public S7SymbolicSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
