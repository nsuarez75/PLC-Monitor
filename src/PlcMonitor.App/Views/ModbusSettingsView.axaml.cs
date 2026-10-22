using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

public sealed partial class ModbusSettingsView : UserControl
{
    public ModbusSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
