using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            window.Closing += async (_, _) => await viewModel.DisposeAsync();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
