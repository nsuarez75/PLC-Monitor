using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Views;

/// <summary>Pide un texto.</summary>
public sealed partial class PromptWindow : Window
{
    public PromptWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public PromptWindow(string title, string message, string initial)
        : this()
    {
        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        var box = this.FindControl<TextBox>("ValueBox")!;
        box.Text = initial;
        Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        this.FindControl<Button>("YesButton")!.Click += (_, _) => Close(box.Text ?? "");
        this.FindControl<Button>("NoButton")!.Click += (_, _) => Close(null);
    }
}
