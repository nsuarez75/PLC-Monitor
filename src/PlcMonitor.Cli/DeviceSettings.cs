using System.ComponentModel;
using System.Text.Json;
using PlcMonitor.Abstractions;
using PlcMonitor.Drivers.S7Absolute;
using PlcMonitor.Drivers.S7Symbolic;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PlcMonitor.Cli;

/// <summary>Opciones de conexión comunes a todos los comandos que hablan con un dispositivo.</summary>
public class DeviceSettings : CommandSettings
{
    public const string PasswordEnvironmentVariable = "PLCMON_PASSWORD";

    [CommandOption("-d|--driver <DRIVER>")]
    [Description("Driver: s7-symbolic (S7-1200/1500, TLS) o s7-absolute (PUT/GET).")]
    [DefaultValue(S7SymbolicDriverFactory.Id)]
    public string Driver { get; set; } = S7SymbolicDriverFactory.Id;

    [CommandOption("-H|--host <HOST>")]
    [Description("IP o nombre del PLC.")]
    public string Host { get; set; } = "";

    [CommandOption("-p|--password <PASSWORD>")]
    [Description("Contraseña de acceso de la CPU (s7-symbolic). Si se omite se usa la variable de entorno PLCMON_PASSWORD.")]
    public string? Password { get; set; }

    [CommandOption("-u|--username <USER>")]
    [Description("Usuario (solo CPUs con gestión de usuarios, s7-symbolic).")]
    public string? Username { get; set; }

    [CommandOption("--cpu <CPU>")]
    [Description("Familia de CPU (s7-absolute): S7300, S7400, S71200, S71500.")]
    [DefaultValue(S7CpuFamily.S71500)]
    public S7CpuFamily Cpu { get; set; } = S7CpuFamily.S71500;

    [CommandOption("--rack <RACK>")]
    [Description("Rack (s7-absolute).")]
    [DefaultValue((short)0)]
    public short Rack { get; set; }

    [CommandOption("--slot <SLOT>")]
    [Description("Slot (s7-absolute): 2 en S7-300, 1 en S7-1500.")]
    [DefaultValue((short)1)]
    public short Slot { get; set; } = 1;

    [CommandOption("--port <PORT>")]
    [Description("Puerto TCP (s7-absolute).")]
    [DefaultValue(102)]
    public int Port { get; set; } = 102;

    [CommandOption("-t|--timeout <MS>")]
    [Description("Timeout en milisegundos.")]
    [DefaultValue(10000)]
    public int TimeoutMs { get; set; } = 10000;

    [CommandOption("--tls-keylog <DIR>")]
    [Description("Diagnóstico: guarda los secretos TLS en DIR para descifrar capturas con Wireshark (s7-symbolic).")]
    public string? TlsKeyLogDirectory { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Muestra el log detallado del driver.")]
    public bool Verbose { get; set; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            return ValidationResult.Error("Falta --host.");
        return ValidationResult.Success();
    }

    /// <summary>Construye la definición del dispositivo para el driver elegido.</summary>
    public DeviceDefinition ToDeviceDefinition(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        object settings = Driver.ToUpperInvariant() switch
        {
            "S7-ABSOLUTE" => new S7AbsoluteSettings
            {
                Host = Host,
                Port = Port,
                Cpu = Cpu,
                Rack = Rack,
                Slot = Slot,
                TimeoutMs = TimeoutMs,
            },
            "S7-SYMBOLIC" => new S7SymbolicSettings
            {
                Host = Host,
                Password = Password ?? environment(PasswordEnvironmentVariable) ?? string.Empty,
                Username = Username ?? string.Empty,
                TimeoutMs = TimeoutMs,
                TlsKeyLogDirectory = TlsKeyLogDirectory,
            },
            _ => throw new ArgumentException($"Driver desconocido '{Driver}'. Usa s7-symbolic o s7-absolute."),
        };
        return new DeviceDefinition(Host, Driver, JsonSerializer.SerializeToElement(settings, settings.GetType(), DriverJson.Options));
    }
}
