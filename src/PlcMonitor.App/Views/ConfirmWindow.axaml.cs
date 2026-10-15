using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

/// <summary>Pregunta sí/no.</summary>
public sealed partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ConfirmWindow(string title, string message)
        : this()
    {
        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        this.FindControl<Button>("YesButton")!.Click += (_, _) => Close(true);
        this.FindControl<Button>("NoButton")!.Click += (_, _) => Close(false);
    }
}
