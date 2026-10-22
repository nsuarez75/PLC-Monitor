using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Drivers.Modbus;
using PlcMonitor.Drivers.S7Absolute;
using PlcMonitor.Drivers.S7Symbolic;
using Spectre.Console;

namespace PlcMonitor.Cli;

/// <summary>Servicios compartidos por los comandos: drivers disponibles, logging y conexión.</summary>
internal static class CliRuntime
{
    public static DriverRegistry Registry { get; } = new([new S7SymbolicDriverFactory(), new S7AbsoluteDriverFactory(), new ModbusDriverFactory()]);

    public static async Task<(IDeviceConnection Connection, ILoggerFactory Logging)> ConnectAsync(DeviceSettings settings, CancellationToken cancellationToken)
    {
        var logging = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
            });
            b.SetMinimumLevel(settings.Verbose ? LogLevel.Debug : LogLevel.Warning);
        });

        var device = settings.ToDeviceDefinition();
        var factory = Registry.Get(device.DriverTypeId);
        var connection = factory.CreateConnection(device, logging);
        try
        {
            await AnsiConsole.Status().StartAsync(
                $"Conectando con [bold]{Markup.Escape(device.Name)}[/] ({factory.DisplayName})…",
                _ => connection.ConnectAsync(cancellationToken));
        }
        catch
        {
            await connection.DisposeAsync();
            logging.Dispose();
            throw;
        }
        return (connection, logging);
    }

    public static string QualityMarkup(QualityCode quality) => quality switch
    {
        QualityCode.Good => "[green]Good[/]",
        QualityCode.Uncertain => "[yellow]Uncertain[/]",
        _ => $"[red]{quality}[/]",
    };
}
