using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PlcMonitor.Abstractions;

/// <summary>
/// Punto de entrada de un driver. Cada protocolo (S7 simbólico, S7 absoluto, Modbus TCP...) aporta una factoría.
/// </summary>
public interface IDriverFactory
{
    /// <summary>Identificador estable del driver, usado en la configuración (p.ej. "s7-symbolic").</summary>
    string TypeId { get; }

    string DisplayName { get; }

    DriverCapabilities Capabilities { get; }

    /// <summary>Tipo de la clase de ajustes del driver (para generar formularios y documentación).</summary>
    Type SettingsType { get; }

    /// <summary>Ajustes por defecto en JSON.</summary>
    JsonElement DefaultSettings();

    /// <summary>Valida los ajustes. Devuelve la lista de errores (vacía si son correctos).</summary>
    IReadOnlyList<string> ValidateSettings(JsonElement settings);

    IDeviceConnection CreateConnection(DeviceDefinition device, ILoggerFactory loggerFactory);
}

/// <summary>Definición de un dispositivo: qué driver usa y con qué ajustes.</summary>
public sealed record DeviceDefinition(string Name, string DriverTypeId, JsonElement Settings, bool Enabled = true);
