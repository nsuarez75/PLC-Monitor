using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

public sealed partial class CsvViewerWindow : Window
{
    public CsvViewerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
