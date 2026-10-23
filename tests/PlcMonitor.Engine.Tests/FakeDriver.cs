using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Engine.Tests;

internal sealed class FakeSettings
{
    public string Host { get; set; } = "";
}

/// <summary>
/// Driver simulado. Las conexiones heredan de <see cref="DeviceConnectionBase"/>, así que el estado, la serialización y
/// la conversión de fallos en calidades son los mismos que en los drivers reales.
/// </summary>
internal sealed class FakeDriverFactory : DriverFactoryBase<FakeSettings>
{
    public const string Id = "fake";

    public ConcurrentDictionary<string, FakeConnection> Connections { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Simulación de cada dispositivo (por nombre). Se crea al primer uso y sobrevive a las reconexiones.</summary>
    public ConcurrentDictionary<string, FakePlc> Plcs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override string TypeId => Id;

    public override string DisplayName => "Simulado";

    public override DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.Browse;

    public FakePlc Plc(string device) => Plcs.GetOrAdd(device, _ => new FakePlc());

    protected override IReadOnlyList<string> Validate(FakeSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Host) ? ["Falta host."] : [];

    protected override IDeviceConnection CreateConnection(string deviceName, FakeSettings settings, ILoggerFactory loggerFactory)
    {
        var connection = new FakeConnection(deviceName, Plc(deviceName));
        Connections[deviceName] = connection;
        return connection;
    }

    public static JsonElement Settings(string host = "10.0.0.1") => JsonSerializer.SerializeToElement(new { host });
}

/// <summary>Estado del PLC simulado: valores en memoria y fallos programables.</summary>
internal sealed class FakePlc
{
    public ConcurrentDictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Número de intentos de conexión que fallarán.</summary>
    public int FailConnects;

    /// <summary>Si es true, las lecturas lanzan IOException (cable desconectado).</summary>
    public volatile bool Unplugged;

    public int ConnectCount;

    public int ReadCount;

    /// <summary>Direcciones que se han leído alguna vez.</summary>
    public ConcurrentDictionary<string, int> ReadAddresses { get; } = new(StringComparer.Ordinal);

    /// <summary>Si no es null, las lecturas leen los valores y esperan a que se complete (un PLC lento que no atiende la cancelación).</summary>
    public TaskCompletionSource? ReadGate { get; set; }

    public ConcurrentQueue<WriteRequest> Writes { get; } = new();
}

internal sealed class FakeConnection(string name, FakePlc plc) : DeviceConnectionBase(name, NullLogger.Instance), IBrowsableConnection
{
    public override DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.Browse;

    /// <summary>Explora las direcciones que tienen valor en el PLC simulado.</summary>
    public Task<BrowseResult> BrowseAsync(BrowseQuery query, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(_ =>
        {
            var all = plc.Values.Keys.Order(StringComparer.Ordinal).ToArray();
            var matches = all.Where(a => string.IsNullOrEmpty(query.Filter) || a.Contains(query.Filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            var items = matches.Take(query.MaxResults).Select(a => new BrowseItem(a, ValidateAddress(a).DataType, "Fake")).ToArray();
            return Task.FromResult(new BrowseResult(items, matches.Length, all.Length));
        }, cancellationToken);

    public override AddressValidation ValidateAddress(string address) =>
        address.StartsWith("BAD", StringComparison.Ordinal)
            ? AddressValidation.Invalid("dirección mala")
            : AddressValidation.Valid(
                address.StartsWith("REAL", StringComparison.Ordinal) ? TagDataType.Float
                : address.Contains(".DBX", StringComparison.Ordinal) ? TagDataType.Boolean
                : TagDataType.Int32);

    protected override Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref plc.ConnectCount);
        if (Interlocked.Decrement(ref plc.FailConnects) >= 0)
            throw new IOException("Conexión rechazada");
        if (plc.Unplugged)
            throw new IOException("Sin respuesta");
        return Task.CompletedTask;
    }

    protected override Task CloseCoreAsync() => Task.CompletedTask;

    protected override async Task<IReadOnlyList<TagValue>> ReadCoreAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        if (plc.Unplugged)
            throw new IOException("Cable desconectado");
        foreach (var address in addresses)
            plc.ReadAddresses.AddOrUpdate(address, 1, (_, n) => n + 1);
        var now = TimeProvider.GetUtcNow();
        IReadOnlyList<TagValue> result = addresses
            .Select(a => plc.Values.TryGetValue(a, out var v)
                ? TagValue.Good(v, ValidateAddress(a).DataType, now)
                : TagValue.Bad(QualityCode.BadAddressInvalid, now, "no existe"))
            .ToArray();
        var gate = plc.ReadGate;
        Interlocked.Increment(ref plc.ReadCount);
        if (gate is not null)
            await gate.Task;
        return result;
    }

    protected override Task<IReadOnlyList<WriteResult>> WriteCoreAsync(IReadOnlyList<WriteRequest> requests, CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            plc.Writes.Enqueue(request);
            plc.Values[request.Address] = request.Value;
        }
        IReadOnlyList<WriteResult> result = requests.Select(r => new WriteResult(r.Address, QualityCode.Good)).ToArray();
        return Task.FromResult(result);
    }
}
