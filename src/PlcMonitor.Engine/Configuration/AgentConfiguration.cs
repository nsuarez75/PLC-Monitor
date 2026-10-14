using System.Text.Json;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Engine.Configuration;

/// <summary>Configuración del motor: los dispositivos que vigila. Se guarda en JSON (agent.json).</summary>
public sealed class AgentConfiguration
{
    public List<DeviceConfiguration> Devices { get; init; } = [];
}

/// <summary>Un dispositivo: driver, ajustes del driver, reconexión y grupos de escaneo con sus tags.</summary>
public sealed class DeviceConfiguration
{
    /// <summary>Nombre único del dispositivo. No puede contener '/' (separa dispositivo y tag).</summary>
    public string Name { get; set; } = "";

    /// <summary>Identificador del driver (p.ej. "s7-absolute").</summary>
    public string Driver { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Primera llave de escritura: si es false no se escribe en ningún tag del dispositivo.</summary>
    public bool WritesEnabled { get; set; }

    /// <summary>Ajustes propios del driver (los mismos que acepta su factoría).</summary>
    public JsonElement Settings { get; set; }

    public ReconnectSettings Reconnect { get; set; } = new();

    public List<ScanGroupConfiguration> ScanGroups { get; init; } = [];

    internal DeviceDefinition ToDeviceDefinition() => new(Name, Driver, Settings, Enabled);
}

/// <summary>Reintentos de conexión con espera exponencial: inicial × multiplicador^n, hasta el máximo.</summary>
public sealed class ReconnectSettings
{
    public int InitialDelayMs { get; set; } = 1000;

    public int MaxDelayMs { get; set; } = 30000;

    public double Multiplier { get; set; } = 2;
}

/// <summary>Grupo de escaneo (scan class): tags que se leen juntos con el mismo periodo.</summary>
public sealed class ScanGroupConfiguration
{
    public string Name { get; set; } = "";

    public int IntervalMs { get; set; } = 1000;

    public List<TagConfiguration> Tags { get; init; } = [];
}

public sealed class TagConfiguration
{
    /// <summary>Nombre del tag, único en el dispositivo. Si se omite se usa la dirección.</summary>
    public string? Name { get; set; }

    /// <summary>Dirección en la sintaxis del driver (p.ej. DB1.DBW10 o "Motor".Velocidad).</summary>
    public string Address { get; set; } = "";

    /// <summary>Segunda llave de escritura: el tag se puede escribir solo si además el dispositivo tiene WritesEnabled.</summary>
    public bool Writable { get; set; }

    /// <summary>Tipo para interpretar los valores escritos cuando el driver no lo deduce de la dirección.</summary>
    public TagDataType? DataType { get; set; }

    internal string EffectiveName => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}
