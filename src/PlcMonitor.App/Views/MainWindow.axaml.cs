using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using PlcMonitor.App.ViewModels;

namespace PlcMonitor.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ListBox>("TagsList")!.DoubleTapped += OnTagDoubleTapped;
    }

    /// <summary>Doble clic en una fila de tags (fuera del campo y los botones de escritura): abre su detalle.</summary>
    private void OnTagDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is TagViewModel tag && DataContext is MainViewModel main)
            main.OpenTagDetailsCommand.Execute(tag);
    }
}
