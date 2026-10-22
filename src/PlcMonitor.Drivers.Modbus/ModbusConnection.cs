using System.Collections.Concurrent;
using System.Globalization;
using FluentModbus;
using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.Modbus;

/// <summary>Conexión con un esclavo Modbus (TCP, RTU o RTU sobre TCP).</summary>
internal sealed class ModbusConnection : DeviceConnectionBase
{
    private readonly ModbusSettings _settings;
    private readonly Func<IModbusTransport> _transportFactory;
    private readonly ConcurrentDictionary<string, (ModbusAddress? Address, string? Error)> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private IModbusTransport? _transport;

    public ModbusConnection(
        string deviceName, ModbusSettings settings, ILogger logger, Func<IModbusTransport>? transportFactory = null,
        SerialBusRegistry? buses = null, TimeProvider? timeProvider = null)
        : base(deviceName, logger, timeProvider)
    {
        _settings = settings;
        _transportFactory = transportFactory ?? (() => new FluentModbusTransport(settings, deviceName, buses ?? SerialBusRegistry.Shared));
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
        var valid = new List<(int Index, ModbusAddress Address)>();
        for (var i = 0; i < addresses.Count; i++)
        {
            var (parsed, error) = Parse(addresses[i]);
            if (parsed is null)
                results[i] = TagValue.Bad(QualityCode.BadAddressInvalid, TimeProvider.GetUtcNow(), error);
            else
                valid.Add((i, parsed));
        }

        foreach (var block in ReadPlanner.Plan(valid, _settings.ReadGap, _settings.MaxRegistersPerRead, _settings.MaxBitsPerRead))
            await ReadBlockAsync(transport, block, results, cancellationToken);
        return results;
    }

    private async Task ReadBlockAsync(IModbusTransport transport, ReadBlock block, TagValue[] results, CancellationToken cancellationToken)
    {
        byte[] data;
        try
        {
            data = await transport.ReadAsync(block.Table, block.Start, block.Count, cancellationToken);
        }
        catch (ModbusException ex) when (ex.ExceptionCode == ModbusExceptionCode.IllegalDataAddress && block.Items.Count > 1)
        {
            // El bloque abarca huecos que el equipo no tiene: se leen sus tags uno a uno para saber cuáles existen.
            foreach (var item in block.Items)
            {
                var single = new ReadBlock(block.Table, item.Address.Offset, item.Address.Count, [item]);
                await ReadBlockAsync(transport, single, results, cancellationToken);
            }
            return;
        }
        catch (Exception ex) when (!IsConnectionLost(ex) && ex is not OperationCanceledException)
        {
            var (quality, detail) = ModbusErrorTranslator.Translate(ex, QualityCode.Bad);
            var bad = TagValue.Bad(quality, TimeProvider.GetUtcNow(), detail);
            foreach (var (index, _) in block.Items)
                results[index] = bad;
            return;
        }

        var timestamp = TimeProvider.GetUtcNow();
        foreach (var (index, address) in block.Items)
        {
            var position = address.Offset - block.Start;
            try
            {
                var value = address.IsBitTable
                    ? ModbusCodec.GetBit(data, position)
                    : ModbusCodec.Decode(address, data.AsSpan(position * 2, address.Count * 2), _settings.ByteOrder);
                results[index] = TagValue.Good(value, address.DataType, timestamp);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                results[index] = TagValue.Bad(QualityCode.BadTypeMismatch, timestamp, $"Respuesta incompleta del equipo: {ex.Message}");
            }
        }
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
            if (!address.IsWritable)
            {
                results[i] = new WriteResult(request.Address, QualityCode.BadAccessDenied,
                    "Las entradas discretas (DI) y los registros de entrada (IR) son de solo lectura en Modbus.");
                continue;
            }

            try
            {
                if (address.Table == ModbusTable.Coil)
                {
                    await transport.WriteCoilAsync(address.Offset, ToBoolean(request.Value), cancellationToken);
                }
                else if (address.IsRegisterBit)
                {
                    // Modbus no escribe bits sueltos de un registro: se lee, se cambia el bit y se escribe el registro entero.
                    // Si el equipo cambia otro bit del mismo registro entre medias, ese cambio se pierde.
                    var bit = ToBoolean(request.Value);
                    var current = await transport.ReadAsync(ModbusTable.HoldingRegister, address.Offset, 1, cancellationToken);
                    await transport.WriteRegistersAsync(address.Offset, ModbusCodec.SetRegisterBit(current, address.BitIndex, bit), cancellationToken);
                }
                else
                {
                    var data = ModbusCodec.Encode(address, request.Value, _settings.ByteOrder);
                    await transport.WriteRegistersAsync(address.Offset, data, cancellationToken);
                }
                results[i] = new WriteResult(request.Address, QualityCode.Good);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
            {
                results[i] = new WriteResult(request.Address, QualityCode.BadTypeMismatch, ex.Message);
            }
            catch (Exception ex) when (!IsConnectionLost(ex) && ex is not OperationCanceledException)
            {
                var (quality, detail) = ModbusErrorTranslator.Translate(ex, QualityCode.Bad);
                results[i] = new WriteResult(request.Address, quality, detail);
            }
        }
        return results;
    }

    protected override Task<IReadOnlyDictionary<string, string>> GetDeviceInfoCoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_transport?.Info ?? new Dictionary<string, string>());

    private static bool ToBoolean(object? value) => value switch
    {
        bool b => b,
        string s when s.Trim() == "1" => true,
        string s when s.Trim() == "0" => false,
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
    };

    private (ModbusAddress? Address, string? Error) Parse(string address) =>
        _parsed.GetOrAdd(address, a => ModbusAddress.TryParse(a, _settings.OneBasedAddresses, out var parsed, out var error) ? (parsed, null) : (null, error));
}
