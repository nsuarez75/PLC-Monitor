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
public sealed record TagDefinition(TagKey Key, string Address, string Group, TimeSpan Interval, bool Writable, TagDataType? DataType);

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
public sealed record DeviceStatus(
    string Device,
    string Driver,
    bool Enabled,
    bool WritesEnabled,
    ConnectionState State,
    string? Detail,
    DateTimeOffset Timestamp,
    DateTimeOffset? NextRetry = null);

/// <summary>Evento que el motor difunde a sus suscriptores.</summary>
public abstract record EngineEvent;

/// <summary>Valores de tags que han cambiado (de valor o de calidad).</summary>
public sealed record TagValuesChanged(IReadOnlyList<TagUpdate> Updates) : EngineEvent;

public sealed record DeviceStatusChanged(DeviceStatus Status) : EngineEvent;

/// <summary>Un dispositivo se ha dado de alta o se ha reconfigurado: sus tags pasan a ser estos.</summary>
public sealed record DeviceConfigured(DeviceStatus Status, IReadOnlyList<TagSnapshot> Tags) : EngineEvent;

/// <summary>Un dispositivo se ha dado de baja (o se ha renombrado).</summary>
public sealed record DeviceRemoved(string Device) : EngineEvent;
