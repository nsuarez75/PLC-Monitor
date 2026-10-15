using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.App.Views;

namespace PlcMonitor.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var path = AppSettings.DefaultPath;
            var viewModel = new MainViewModel(new GrpcAgentConnector(), new AvaloniaDispatcher(), new WindowDialogs(window), AppSettings.Load(path), path);
            window.DataContext = viewModel;
            ApplyTheme(viewModel.Theme);
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Theme))
                    ApplyTheme(viewModel.Theme);
            };
            window.Closing += async (_, _) => await viewModel.DisposeAsync();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>"light", "dark" o cualquier otro valor para seguir al sistema.</summary>
    public void ApplyTheme(string theme) => RequestedThemeVariant = theme switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
