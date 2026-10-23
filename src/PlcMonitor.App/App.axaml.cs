using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlcMonitor.App.Localization;
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
            var path = AppSettings.DefaultPath;
            var settings = AppSettings.Load(path);
            // El idioma se fija antes de crear la ventana: el XAML se evalúa al construirla.
            Loc.SetLanguage(settings.Language);
            // Las gráficas pintan sus textos a mano: al cambiar de idioma se repintan para traducirlos.
            Loc.LanguageChanged += (_, _) =>
            {
                foreach (var visual in desktop.Windows.SelectMany(w => w.GetVisualDescendants()))
                    visual.InvalidateVisual();
            };
            var window = new MainWindow();
            var viewModel = new MainViewModel(new GrpcAgentConnector(), new AvaloniaDispatcher(), new WindowDialogs(window), settings, path);
            window.DataContext = viewModel;
            ApplyTheme(viewModel.Theme);
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Theme))
                    ApplyTheme(viewModel.Theme);
            };
            window.Closing += async (_, _) => await viewModel.DisposeAsync();
            // Red de seguridad: un fallo inesperado en la interfaz se muestra en la barra de estado en vez de cerrar la app.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                System.Diagnostics.Trace.WriteLine(e.Exception);
                viewModel.Report(Loc.F("Error inesperado: {0}", e.Exception.Message), isError: true);
            };
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
