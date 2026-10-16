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

    /// <summary>Historizar solo mientras otro tag del dispositivo cumple una condición; null = siempre.</summary>
    public HistoryTrigger? Trigger { get; set; }
}

public enum TriggerCondition
{
    Equal,
    NotEqual,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
}

/// <summary>
/// Disparo del histórico: el tag se guarda mientras <see cref="Tag"/> (del mismo dispositivo) cumple
/// <see cref="Condition"/> con <see cref="Value"/>, y <see cref="HoldMs"/> más después de que deje de cumplirse.
/// </summary>
public sealed class HistoryTrigger
{
    /// <summary>Nombre del tag que dispara (del mismo dispositivo).</summary>
    public string Tag { get; set; } = "";

    public TriggerCondition Condition { get; set; } = TriggerCondition.Equal;

    /// <summary>Valor con el que se compara: true/false para booleanos, un número para los demás (= y ≠ admiten texto).</summary>
    public string Value { get; set; } = "true";

    /// <summary>Tiempo que se sigue guardando después de que la condición deje de cumplirse.</summary>
    public int HoldMs { get; set; }

    /// <summary>¿Cumple <paramref name="value"/> la condición? Un valor ausente o no comparable no la cumple.</summary>
    public bool IsMet(object? value)
    {
        if (value is null)
            return false;
        if (TryNumber(value, out var actual) && TryNumber(Value.Trim(), out var target))
        {
            return Condition switch
            {
                TriggerCondition.Equal => actual == target,
                TriggerCondition.NotEqual => actual != target,
                TriggerCondition.Greater => actual > target,
                TriggerCondition.GreaterOrEqual => actual >= target,
                TriggerCondition.Less => actual < target,
                TriggerCondition.LessOrEqual => actual <= target,
                _ => false,
            };
        }
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        return Condition switch
        {
            TriggerCondition.Equal => string.Equals(text, Value.Trim(), StringComparison.Ordinal),
            TriggerCondition.NotEqual => !string.Equals(text, Value.Trim(), StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>Número de un valor (bool como 0/1; "true"/"false" también).</summary>
    public static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case bool b:
                number = b ? 1 : 0;
                return true;
            case string s when bool.TryParse(s, out var b):
                number = b ? 1 : 0;
                return true;
            case string s:
                return double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number);
            case float f:
                number = double.Parse(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
                return true;
            case sbyte or byte or short or ushort or int or uint or long or ulong or double or decimal:
                number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            default:
                number = 0;
                return false;
        }
    }
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
