namespace PlcMonitor.Abstractions;

/// <summary>
/// Conexión con un dispositivo concreto. Cada driver la implementa para su protocolo.
/// Las implementaciones deben admitir llamadas concurrentes (las serializan internamente).
/// </summary>
public interface IDeviceConnection : IAsyncDisposable
{
    string DeviceName { get; }

    DriverCapabilities Capabilities { get; }

    ConnectionState State { get; }

    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Comprueba la sintaxis de una dirección sin acceder al dispositivo.</summary>
    AddressValidation ValidateAddress(string address);

    /// <summary>Lee varias direcciones en una sola operación. Devuelve un resultado por dirección, en el mismo orden.</summary>
    Task<IReadOnlyList<TagValue>> ReadAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken = default);

    /// <summary>Escribe varios valores. Devuelve un resultado por petición, en el mismo orden.</summary>
    Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>Información descriptiva del dispositivo (modelo, firmware...). Vacío si no está disponible.</summary>
    Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(CancellationToken cancellationToken = default);
}

public readonly record struct AddressValidation(bool IsValid, string? Error = null, TagDataType DataType = TagDataType.Unknown)
{
    public static AddressValidation Valid(TagDataType dataType = TagDataType.Unknown) => new(true, null, dataType);

    public static AddressValidation Invalid(string error) => new(false, error);
}

public sealed record WriteRequest(string Address, object? Value);

public sealed record WriteResult(string Address, QualityCode Quality, string? ErrorDetail = null)
{
    public bool Success => Quality == QualityCode.Good;
}
