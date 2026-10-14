using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;
using S7.Net;

namespace PlcMonitor.Drivers.S7Absolute;

/// <summary>Conexión S7 por direccionamiento absoluto (S7comm PUT/GET).</summary>
internal sealed class S7AbsoluteConnection : DeviceConnectionBase
{
    private readonly S7AbsoluteSettings _settings;
    private readonly Func<IS7Transport> _transportFactory;
    private readonly ConcurrentDictionary<string, (S7Address? Address, string? Error)> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private IS7Transport? _transport;

    public S7AbsoluteConnection(
        string deviceName, S7AbsoluteSettings settings, ILogger logger, Func<IS7Transport>? transportFactory = null, TimeProvider? timeProvider = null)
        : base(deviceName, logger, timeProvider)
    {
        _settings = settings;
        _transportFactory = transportFactory ?? (() => new S7NetTransport(settings));
    }

    public override DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write;

    public override AddressValidation ValidateAddress(string address)
    {
        var (parsed, error) = Parse(address);
        return parsed is null ? AddressValidation.Invalid(error!) : AddressValidation.Valid(parsed.DataType);
    }

    protected override async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        _transport?.Dispose();
        _transport = _transportFactory();
        await _transport.OpenAsync(cancellationToken);
    }

    protected override Task CloseCoreAsync()
    {
        var transport = _transport;
        _transport = null;
        transport?.Close();
        transport?.Dispose();
        return Task.CompletedTask;
    }

    protected override async Task<IReadOnlyList<TagValue>> ReadCoreAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        var transport = _transport ?? throw new IOException("Transporte cerrado.");
        var results = new TagValue[addresses.Count];
        var valid = new List<(int Index, S7Address Address)>();
        for (var i = 0; i < addresses.Count; i++)
        {
            var (parsed, error) = Parse(addresses[i]);
            if (parsed is null)
                results[i] = TagValue.Bad(QualityCode.BadAddressInvalid, TimeProvider.GetUtcNow(), error);
            else
                valid.Add((i, parsed));
        }

        foreach (var block in ReadPlanner.Plan(valid, _settings.ReadGapBytes))
        {
            byte[] data;
            try
            {
                data = await transport.ReadBytesAsync(block.Area, block.DbNumber, block.Start, block.Length, cancellationToken);
            }
            catch (Exception ex) when (!IsConnectionLost(ex) && ex is not OperationCanceledException)
            {
                // P.ej. DB inexistente, área fuera de rango o PUT/GET deshabilitado: solo afecta a este bloque.
                var (quality, detail) = S7ErrorTranslator.Translate(ex, QualityCode.BadAddressInvalid);
                var bad = TagValue.Bad(quality, TimeProvider.GetUtcNow(), detail);
                foreach (var (index, _) in block.Items)
                    results[index] = bad;
                continue;
            }

            var timestamp = TimeProvider.GetUtcNow();
            foreach (var (index, address) in block.Items)
            {
                var slice = data.AsSpan(address.ByteOffset - block.Start, address.ByteLength);
                try
                {
                    results[index] = TagValue.Good(S7Codec.Decode(address, slice), address.DataType, timestamp);
                }
                catch (ArgumentException ex)
                {
                    results[index] = TagValue.Bad(QualityCode.BadTypeMismatch, timestamp, ex.Message);
                }
            }
        }
        return results;
    }

    protected override async Task<IReadOnlyList<WriteResult>> WriteCoreAsync(IReadOnlyList<WriteRequest> requests, CancellationToken cancellationToken)
    {
        var transport = _transport ?? throw new IOException("Transporte cerrado.");
        var results = new WriteResult[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var (address, parseError) = Parse(request.Address);
            if (address is null)
            {
                results[i] = new WriteResult(request.Address, QualityCode.BadAddressInvalid, parseError);
                continue;
            }

            try
            {
                if (address.ValueType == S7ValueType.Bit)
                {
                    var bit = Convert.ToBoolean(request.Value, System.Globalization.CultureInfo.InvariantCulture);
                    await transport.WriteBitAsync(address.Area, address.DbNumber, address.ByteOffset, address.BitIndex, bit, cancellationToken);
                }
                else
                {
                    var data = S7Codec.Encode(address, request.Value);
                    await transport.WriteBytesAsync(address.Area, address.DbNumber, address.ByteOffset, data, cancellationToken);
                }
                results[i] = new WriteResult(request.Address, QualityCode.Good);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
            {
                results[i] = new WriteResult(request.Address, QualityCode.BadTypeMismatch, ex.Message);
            }
            catch (Exception ex) when (!IsConnectionLost(ex) && ex is not OperationCanceledException)
            {
                var (quality, detail) = S7ErrorTranslator.Translate(ex, QualityCode.Bad);
                results[i] = new WriteResult(request.Address, quality, detail);
            }
        }
        return results;
    }

    protected override Task<IReadOnlyDictionary<string, string>> GetDeviceInfoCoreAsync(CancellationToken cancellationToken) =>
        _transport?.GetInfoAsync(cancellationToken) ?? Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    protected override bool IsConnectionLost(Exception exception) =>
        base.IsConnectionLost(exception)
        || exception is PlcException { ErrorCode: ErrorCode.ConnectionError or ErrorCode.IPAddressNotAvailable }
        || (exception is PlcException && exception.InnerException is { } inner && base.IsConnectionLost(inner));

    private (S7Address? Address, string? Error) Parse(string address) =>
        _parsed.GetOrAdd(address, static a => S7Address.TryParse(a, out var parsed, out var error) ? (parsed, null) : (null, error));
}
