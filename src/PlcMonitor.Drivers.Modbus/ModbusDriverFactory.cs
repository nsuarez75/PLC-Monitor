using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.Modbus;

/// <summary>Driver Modbus: TCP, RTU por puerto serie (RS-485/RS-232) y RTU sobre TCP. No se puede explorar: los tags se dan de alta por dirección.</summary>
public sealed class ModbusDriverFactory : DriverFactoryBase<ModbusSettings>
{
    public const string Id = "modbus";

    private readonly SerialBusRegistry _buses;

    public ModbusDriverFactory()
        : this(SerialBusRegistry.Shared)
    {
    }

    internal ModbusDriverFactory(SerialBusRegistry buses)
    {
        _buses = buses;
    }

    public override string TypeId => Id;

    public override string DisplayName => "Modbus TCP / RTU";

    public override DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write;

    protected override IReadOnlyList<string> Validate(ModbusSettings settings)
    {
        var errors = new List<string>();
        if (settings.Transport == ModbusTransport.Rtu)
        {
            if (string.IsNullOrWhiteSpace(settings.SerialPort))
                errors.Add("Puerto serie obligatorio (p.ej. COM3 o /dev/ttyUSB0).");
            if (settings.BaudRate is < 300 or > 921600)
                errors.Add("Baudios fuera de rango (300-921600).");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.Host))
                errors.Add("Host obligatorio.");
            if (settings.Port is < 1 or > 65535)
                errors.Add("Puerto fuera de rango (1-65535).");
        }
        // En serie (y tras una pasarela) los esclavos van del 1 al 247; en TCP directo el equipo suele aceptar cualquiera.
        if (settings.Transport == ModbusTransport.Tcp ? settings.UnitId is < 0 or > 255 : settings.UnitId is < 1 or > 247)
            errors.Add(settings.Transport == ModbusTransport.Tcp ? "Unit id fuera de rango (0-255)." : "Unit id fuera de rango (1-247).");
        if (settings.TimeoutMs is < 100 or > 60000)
            errors.Add("Timeout fuera de rango (100-60000 ms).");
        if (settings.MaxRegistersPerRead is < 1 or > 125)
            errors.Add("Registros por petición fuera de rango (1-125).");
        if (settings.MaxBitsPerRead is < 1 or > 2000)
            errors.Add("Bits por petición fuera de rango (1-2000).");
        if (settings.ReadGap is < 0 or > 100)
            errors.Add("ReadGap fuera de rango (0-100).");
        return errors;
    }

    protected override IDeviceConnection CreateConnection(string deviceName, ModbusSettings settings, ILoggerFactory loggerFactory) =>
        new ModbusConnection(deviceName, settings, loggerFactory.CreateLogger<ModbusConnection>(), buses: _buses);
}
