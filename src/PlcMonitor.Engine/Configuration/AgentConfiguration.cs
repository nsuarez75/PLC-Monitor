using System.Text.Json;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Engine.Configuration;

/// <summary>Configuración del motor: los dispositivos que vigila. Se guarda en JSON (agent.json).</summary>
public sealed class AgentConfiguration
{
    public List<DeviceConfiguration> Devices { get; init; } = [];

    /// <summary>Historiador: dónde y cuánto tiempo se guardan los tags con historización activada.</summary>
    public HistorianSettings Historian { get; set; } = new();
}

/// <summary>Ajustes globales del historiador.</summary>
public sealed class HistorianSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Carpeta de los ficheros (uno por mes). Relativa a la carpeta del agente.</summary>
    public string Path { get; set; } = "history";

    /// <summary>Días que se conservan las muestras (0 = sin límite).</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Cada cuánto se vuelcan a disco las muestras acumuladas.</summary>
    public int FlushIntervalMs { get; set; } = 1000;
}

public enum DeadbandMode
{
    Absolute,
    Percent,
}

/// <summary>Historización de un tag (estilo Ignition).</summary>
public sealed class TagHistorySettings
{
    public bool Enabled { get; set; }

    /// <summary>Intervalo mínimo entre muestras guardadas (0 = cada cambio). Si llegan cambios más rápido, se guarda el último al cumplirse el intervalo.</summary>
    public int MinIntervalMs { get; set; }

    /// <summary>Banda muerta: solo se guarda si el valor numérico cambia más que esto (0 = cualquier cambio).</summary>
    public double Deadband { get; set; }

    public DeadbandMode DeadbandMode { get; set; } = DeadbandMode.Absolute;

    /// <summary>Tiempo máximo sin guardar: se repite el valor actual aunque no cambie (0 = nunca).</summary>
    public int MaxIntervalMs { get; set; }
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

    /// <summary>Historización del tag; null = no se historiza.</summary>
    public TagHistorySettings? History { get; set; }

    internal string EffectiveName => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}
