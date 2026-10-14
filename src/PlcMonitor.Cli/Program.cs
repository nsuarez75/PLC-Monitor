using PlcMonitor.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("plcmon");
    config.SetExceptionHandler((ex, _) =>
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
        return 1;
    });

    config.AddCommand<DriversCommand>("drivers").WithDescription("Lista los drivers disponibles.");
    config.AddCommand<InfoCommand>("info")
        .WithDescription("Conecta y muestra información del dispositivo.")
        .WithExample("info", "-H", "192.168.2.1", "-p", "secreto");
    config.AddCommand<BrowseCommand>("browse")
        .WithDescription("Busca variables en la CPU (s7-symbolic).")
        .WithExample("browse", "Motor", "-H", "192.168.2.1", "-n", "50");
    config.AddCommand<ReadCommand>("read")
        .WithDescription("Lee una o varias direcciones.")
        .WithExample("read", "\"MiDB\".Motor.Velocidad", "-H", "192.168.2.1")
        .WithExample("read", "DB1.DBW10", "DB1.REAL20", "-d", "s7-absolute", "-H", "192.168.0.10", "--cpu", "S7300", "--slot", "2");
    config.AddCommand<WatchCommand>("watch")
        .WithDescription("Lee periódicamente y muestra los cambios (Ctrl+C para salir).")
        .WithExample("watch", "\"MiDB\".Contador", "-H", "192.168.2.1", "-i", "500");
    config.AddCommand<WriteCommand>("write")
        .WithDescription("Escribe un valor (pide confirmación).")
        .WithExample("write", "\"MiDB\".Consigna", "12.5", "-H", "192.168.2.1");
});

return await app.RunAsync(args, cts.Token);
