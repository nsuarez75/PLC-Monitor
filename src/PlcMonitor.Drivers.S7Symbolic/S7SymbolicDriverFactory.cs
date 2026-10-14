using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>Driver S7 simbólico: S7CommPlus con TLS para S7-1200 (FW ≥ 4.3) y S7-1500 (FW ≥ 2.9).</summary>
public sealed class S7SymbolicDriverFactory : DriverFactoryBase<S7SymbolicSettings>
{
    public const string Id = "s7-symbolic";

    public override string TypeId => Id;

    public override string DisplayName => "Siemens S7 simbólico (S7CommPlus/TLS)";

    public override DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.Browse;

    protected override IReadOnlyList<string> Validate(S7SymbolicSettings settings)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Host))
            errors.Add("Host obligatorio.");
        if (settings.TimeoutMs < 500)
            errors.Add("Timeout mínimo: 500 ms.");
        if (!string.IsNullOrEmpty(settings.Username) && string.IsNullOrEmpty(settings.Password))
            errors.Add("Si se indica usuario, la contraseña es obligatoria.");
        if (settings.TlsKeyLogDirectory is { Length: > 0 } dir && !Directory.Exists(dir))
            errors.Add($"El directorio de claves TLS '{dir}' no existe.");
        return errors;
    }

    protected override IDeviceConnection CreateConnection(string deviceName, S7SymbolicSettings settings, ILoggerFactory loggerFactory) =>
        new S7SymbolicConnection(deviceName, settings, loggerFactory.CreateLogger<S7SymbolicConnection>());
}
