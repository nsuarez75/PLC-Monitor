using System.ComponentModel;
using System.Text.Json;
using PlcMonitor.Abstractions;
using PlcMonitor.Drivers.Modbus;
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
    [Description("Driver: s7-symbolic (S7-1200/1500, acceso simbólico), s7-absolute (PUT/GET) o modbus (TCP, RTU por puerto serie o RTU sobre TCP).")]
    [DefaultValue(S7SymbolicDriverFactory.Id)]
    public string Driver { get; set; } = S7SymbolicDriverFactory.Id;

    [CommandOption("-H|--host <HOST>")]
    [Description("IP o nombre del PLC (en Modbus RTU, no se usa: va --serial).")]
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
    [Description("Puerto TCP (s7-absolute: 102 por defecto; modbus: 502).")]
    public int? Port { get; set; }

    [CommandOption("--serial <PUERTO>")]
    [Description("Puerto serie para Modbus RTU (COM3, /dev/ttyUSB0...).")]
    public string? SerialPort { get; set; }

    [CommandOption("--rtu-over-tcp")]
    [Description("Modbus RTU sobre TCP (convertidor serie-Ethernet en modo transparente) en --host/--port.")]
    public bool RtuOverTcp { get; set; }

    [CommandOption("--baud <BAUDIOS>")]
    [Description("Baudios (Modbus RTU).")]
    [DefaultValue(9600)]
    public int BaudRate { get; set; } = 9600;

    [CommandOption("--parity <PARIDAD>")]
    [Description("Paridad (Modbus RTU): none, even u odd.")]
    [DefaultValue(ModbusParity.None)]
    public ModbusParity Parity { get; set; } = ModbusParity.None;

    [CommandOption("--stop-bits <BITS>")]
    [Description("Bits de parada (Modbus RTU): one o two.")]
    [DefaultValue(ModbusStopBits.One)]
    public ModbusStopBits StopBits { get; set; } = ModbusStopBits.One;

    [CommandOption("--unit-id <N>")]
    [Description("Número de esclavo Modbus (unit id).")]
    [DefaultValue(1)]
    public int UnitId { get; set; } = 1;

    [CommandOption("--byte-order <ORDEN>")]
    [Description("Orden de bytes Modbus de los valores de 32/64 bits: ABCD, BADC, CDAB o DCBA.")]
    [DefaultValue(ModbusByteOrder.ABCD)]
    public ModbusByteOrder ByteOrder { get; set; } = ModbusByteOrder.ABCD;

    [CommandOption("--one-based")]
    [Description("Las direcciones Modbus por tabla (HR1, C1...) empiezan en 1.")]
    public bool OneBased { get; set; }

    [CommandOption("-t|--timeout <MS>")]
    [Description("Timeout en milisegundos.")]
    [DefaultValue(10000)]
    public int TimeoutMs { get; set; } = 10000;

    [CommandOption("--security <MODE>")]
    [Description("Seguridad (s7-symbolic): auto (TLS y, si la CPU no lo admite, sin TLS), tls o legacy (sin TLS).")]
    [DefaultValue(S7SymbolicSecurity.Auto)]
    public S7SymbolicSecurity Security { get; set; } = S7SymbolicSecurity.Auto;

    [CommandOption("-v|--verbose")]
    [Description("Muestra el log detallado del driver.")]
    public bool Verbose { get; set; }

    public override ValidationResult Validate()
    {
        if (IsModbusRtu)
            return string.IsNullOrWhiteSpace(SerialPort) ? ValidationResult.Error("Falta --serial.") : ValidationResult.Success();
        if (string.IsNullOrWhiteSpace(Host))
            return ValidationResult.Error("Falta --host.");
        return ValidationResult.Success();
    }

    private bool IsModbusRtu => Driver.Equals(ModbusDriverFactory.Id, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(SerialPort);

    /// <summary>Construye la definición del dispositivo para el driver elegido.</summary>
    public DeviceDefinition ToDeviceDefinition(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        object settings = Driver.ToUpperInvariant() switch
        {
            "S7-ABSOLUTE" => new S7AbsoluteSettings
            {
                Host = Host,
                Port = Port ?? 102,
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
                Security = Security,
            },
            "MODBUS" => new ModbusSettings
            {
                Transport = IsModbusRtu ? ModbusTransport.Rtu : RtuOverTcp ? ModbusTransport.RtuOverTcp : ModbusTransport.Tcp,
                Host = Host,
                Port = Port ?? 502,
                SerialPort = SerialPort ?? string.Empty,
                BaudRate = BaudRate,
                Parity = Parity,
                StopBits = StopBits,
                UnitId = UnitId,
                TimeoutMs = TimeoutMs,
                ByteOrder = ByteOrder,
                OneBasedAddresses = OneBased,
            },
            _ => throw new ArgumentException($"Driver desconocido '{Driver}'. Usa s7-symbolic, s7-absolute o modbus."),
        };
        var name = IsModbusRtu ? SerialPort! : Host;
        return new DeviceDefinition(name, Driver, JsonSerializer.SerializeToElement(settings, settings.GetType(), DriverJson.Options));
    }
}
