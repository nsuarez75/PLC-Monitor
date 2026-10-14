using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcMonitor.Abstractions;

/// <summary>Opciones JSON comunes para los ajustes de drivers y la configuración.</summary>
public static class DriverJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };
}
