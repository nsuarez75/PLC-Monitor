using Avalonia;
using Avalonia.Headless;

namespace PlcMonitor.App.Tests;

/// <summary>
/// La app real (estilos incluidos) sin ventana, con render Skia para poder capturar imágenes. Una sola sesión para
/// todos los tests de interfaz: Avalonia solo admite una aplicación por proceso.
/// </summary>
public static class HeadlessApp
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Ejecuta <paramref name="action"/> en el hilo de la UI de Avalonia.</summary>
    public static Task RunAsync(Action action) => Session.Value.Dispatch(action, CancellationToken.None);

    /// <summary>Ejecuta <paramref name="action"/> (asíncrona) en el hilo de la UI de Avalonia.</summary>
    public static Task RunAsync(Func<Task> action) => Session.Value.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);
}
