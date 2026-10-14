using PlcMonitor.Abstractions;

namespace PlcMonitor.Core.Tests;

/// <summary>Conexión simulada: devuelve los valores del diccionario o lanza la excepción configurada.</summary>
internal sealed class FakeDeviceConnection(string name = "fake") : IDeviceConnection
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    public Exception? ReadException { get; set; }

    public int ReadCount { get; private set; }

    public string DeviceName { get; } = name;

    public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write;

    public ConnectionState State { get; set; } = ConnectionState.Connected;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        State = ConnectionState.Connected;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(State, null));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public AddressValidation ValidateAddress(string address) => AddressValidation.Valid();

    public Task<IReadOnlyList<TagValue>> ReadAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        if (ReadException is not null)
            throw ReadException;
        var now = DateTimeOffset.UnixEpoch;
        IReadOnlyList<TagValue> result = addresses
            .Select(a => Values.TryGetValue(a, out var v)
                ? TagValue.Good(v, TagDataType.Int32, now)
                : TagValue.Bad(QualityCode.BadAddressInvalid, now))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> requests, CancellationToken cancellationToken = default)
    {
        foreach (var r in requests)
            Values[r.Address] = r.Value;
        IReadOnlyList<WriteResult> result = requests.Select(r => new WriteResult(r.Address, QualityCode.Good)).ToArray();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
