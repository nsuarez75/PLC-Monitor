using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

public sealed partial class TransformsView : UserControl
{
    public TransformsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
