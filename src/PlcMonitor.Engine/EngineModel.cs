using PlcMonitor.Abstractions;

namespace PlcMonitor.Engine;

/// <summary>Identifica un tag dentro del motor: dispositivo + nombre del tag.</summary>
public readonly record struct TagKey(string Device, string Tag)
{
    public override string ToString() => $"{Device}/{Tag}";
}

/// <summary>Nuevo valor de un tag.</summary>
public sealed record TagUpdate(TagKey Key, TagValue Value);

/// <summary>Definición de un tag tal como la usa el motor.</summary>
public sealed record TagDefinition(TagKey Key, string Address, string Group, TimeSpan Interval, bool Writable, TagDataType? DataType)
{
    /// <summary>Historización (null o Enabled=false = no se historiza).</summary>
    public Configuration.TagHistorySettings? History { get; init; }

    public bool IsHistorized => Enabled && History is { Enabled: true };

    /// <summary>Transformación con la que los clientes muestran el valor; null = ninguna.</summary>
    public string? Transform { get; init; }

    /// <summary>Grupo de visualización; null = sin grupo.</summary>
    public string? Folder { get; init; }

    /// <summary>Unidad con la que los clientes muestran el valor; null = ninguna.</summary>
    public string? Unit { get; init; }

    /// <summary>Decimales con los que los clientes muestran los reales; null = el valor completo.</summary>
    public int? Decimals { get; init; }

    /// <summary>Escalado a unidades de ingeniería que el motor aplica al leer y, a la inversa, al escribir; null = ninguno.</summary>
    public Configuration.TagScaling? Scaling { get; init; }

    /// <summary>Segunda llave de publicación en OPC UA (la primera es la del dispositivo).</summary>
    public bool OpcUaPublished { get; init; } = true;

    /// <summary>Tag activo: uno desactivado no se lee, no se historiza, no se publica en OPC UA ni se escribe.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Se publica en OPC UA: activo y con la segunda llave (la primera es la del dispositivo).</summary>
    public bool IsOpcUaPublished => Enabled && OpcUaPublished;
}

/// <summary>Definición de un tag con su último valor conocido.</summary>
public sealed record TagSnapshot(TagDefinition Definition, TagValue Value);

/// <summary>Estado de un dispositivo en el motor.</summary>
/// <param name="Device">Nombre del dispositivo.</param>
/// <param name="Driver">Identificador del driver.</param>
/// <param name="Enabled">Si el motor lo está vigilando.</param>
/// <param name="WritesEnabled">Primera llave de escritura.</param>
/// <param name="State">Estado de la conexión.</param>
/// <param name="Detail">Motivo del último error o cambio.</param>
/// <param name="Timestamp">Momento del cambio de estado.</param>
/// <param name="NextRetry">Próximo intento de conexión, si está esperando para reconectar.</param>
/// <param name="OpcUaPublished">Primera llave de publicación en OPC UA.</param>
public sealed record DeviceStatus(
    string Device,
    string Driver,
    bool Enabled,
    bool WritesEnabled,
    ConnectionState State,
    string? Detail,
    DateTimeOffset Timestamp,
    DateTimeOffset? NextRetry = null,
    bool OpcUaPublished = false);

/// <summary>Evento que el motor difunde a sus suscriptores.</summary>
public abstract record EngineEvent;

/// <summary>Valores de tags que han cambiado (de valor o de calidad).</summary>
public sealed record TagValuesChanged(IReadOnlyList<TagUpdate> Updates) : EngineEvent;

public sealed record DeviceStatusChanged(DeviceStatus Status) : EngineEvent;

/// <summary>Un dispositivo se ha dado de alta o se ha reconfigurado: sus tags pasan a ser estos.</summary>
public sealed record DeviceConfigured(DeviceStatus Status, IReadOnlyList<TagSnapshot> Tags) : EngineEvent;

/// <summary>Un dispositivo se ha dado de baja (o se ha renombrado).</summary>
public sealed record DeviceRemoved(string Device) : EngineEvent;

/// <summary>Ha cambiado el orden de los dispositivos: estos son todos, en el orden nuevo.</summary>
public sealed record DevicesReordered(IReadOnlyList<string> Devices) : EngineEvent;

/// <summary>Han cambiado las transformaciones: estas sustituyen a todas las anteriores.</summary>
public sealed record TransformsChanged(IReadOnlyList<Configuration.ValueTransform> Transforms) : EngineEvent;
