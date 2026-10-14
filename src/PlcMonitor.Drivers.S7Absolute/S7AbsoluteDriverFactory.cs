using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.S7Absolute;

/// <summary>Driver S7 absoluto: S7comm clásico (PUT/GET) para S7-300/400/1200/1500.</summary>
public sealed class S7AbsoluteDriverFactory : DriverFactoryBase<S7AbsoluteSettings>
{
    public const string Id = "s7-absolute";

    public override string TypeId => Id;

    public override string DisplayName => "Siemens S7 absoluto (PUT/GET)";

    public override DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write;

    protected override IReadOnlyList<string> Validate(S7AbsoluteSettings settings)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Host))
            errors.Add("Host obligatorio.");
        if (settings.Port is < 1 or > 65535)
            errors.Add("Puerto fuera de rango (1-65535).");
        if (settings.Rack is < 0 or > 7)
            errors.Add("Rack fuera de rango (0-7).");
        if (settings.Slot is < 0 or > 31)
            errors.Add("Slot fuera de rango (0-31).");
        if (settings.TimeoutMs < 100)
            errors.Add("Timeout mínimo: 100 ms.");
        if (settings.ReadGapBytes is < 0 or > 1024)
            errors.Add("ReadGapBytes fuera de rango (0-1024).");
        return errors;
    }

    protected override IDeviceConnection CreateConnection(string deviceName, S7AbsoluteSettings settings, ILoggerFactory loggerFactory) =>
        new S7AbsoluteConnection(deviceName, settings, loggerFactory.CreateLogger<S7AbsoluteConnection>());
}
