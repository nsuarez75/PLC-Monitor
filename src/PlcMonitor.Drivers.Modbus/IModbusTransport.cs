using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FluentModbus;

namespace PlcMonitor.Drivers.Modbus;

/// <summary>Acceso de bajo nivel al esclavo (registros en bytes de cable, bits empaquetados). Permite probar sin equipo.</summary>
internal interface IModbusTransport : IDisposable
{
    Task OpenAsync(CancellationToken cancellationToken);

    void Close();

    /// <summary>Lee <paramref name="count"/> registros (2 bytes cada uno) o bits (empaquetados, el primero en el bit 0).</summary>
    Task<byte[]> ReadAsync(ModbusTable table, int start, int count, CancellationToken cancellationToken);

    Task WriteRegistersAsync(int start, byte[] data, CancellationToken cancellationToken);

    Task WriteCoilAsync(int offset, bool value, CancellationToken cancellationToken);

    IReadOnlyDictionary<string, string> Info { get; }
}

/// <summary>Transporte real con FluentModbus (MIT): Modbus TCP, RTU por puerto serie (bus compartido) o RTU sobre TCP.</summary>
internal sealed class FluentModbusTransport : IModbusTransport
{
    private readonly ModbusSettings _settings;
    private readonly string _deviceName;
    private readonly SerialBusRegistry _buses;
    private ModbusClient? _client;
    private SerialBus? _bus;

    public FluentModbusTransport(ModbusSettings settings, string deviceName, SerialBusRegistry buses)
    {
        _settings = settings;
        _deviceName = deviceName;
        _buses = buses;
    }

    private byte Unit => (byte)_settings.UnitId;

    public IReadOnlyDictionary<string, string> Info => new Dictionary<string, string>
    {
        ["Transport"] = _settings.Transport switch
        {
            ModbusTransport.Rtu => $"Modbus RTU {_settings.SerialPort} ({SerialBus.SignatureOf(_settings)})",
            ModbusTransport.RtuOverTcp => $"Modbus RTU sobre TCP {_settings.Host}:{_settings.Port.ToString(CultureInfo.InvariantCulture)}",
            _ => $"Modbus TCP {_settings.Host}:{_settings.Port.ToString(CultureInfo.InvariantCulture)}",
        },
        ["UnitId"] = _settings.UnitId.ToString(CultureInfo.InvariantCulture),
        ["ByteOrder"] = _settings.ByteOrder.ToString(),
        ["AddressBase"] = _settings.OneBasedAddresses ? "1" : "0",
        ["SharedBus"] = _bus is { } bus ? string.Join(", ", bus.Devices.Order(StringComparer.Ordinal)) : "",
    }.Where(kv => kv.Value.Length > 0).ToDictionary();

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        Close();
        if (_settings.Transport == ModbusTransport.Rtu)
        {
            // Abrir un puerto serie es rápido pero síncrono.
            _bus = await Task.Run(() => _buses.Acquire(_settings, _deviceName), cancellationToken);
            return;
        }

        var endpoint = new IPEndPoint(await ResolveAsync(_settings.Host, cancellationToken), _settings.Port);
        if (_settings.Transport == ModbusTransport.RtuOverTcp)
        {
            var client = new ModbusRtuOverTcpClient { ConnectTimeout = _settings.TimeoutMs, ReadTimeout = _settings.TimeoutMs, WriteTimeout = _settings.TimeoutMs };
            _client = client;
            await Task.Run(() => client.Connect(endpoint, ModbusEndianness.BigEndian), cancellationToken);
        }
        else
        {
            var client = new ModbusTcpClient { ConnectTimeout = _settings.TimeoutMs, ReadTimeout = _settings.TimeoutMs, WriteTimeout = _settings.TimeoutMs };
            _client = client;
            await Task.Run(() => client.Connect(endpoint, ModbusEndianness.BigEndian), cancellationToken);
        }
    }

    public void Close()
    {
        switch (_client)
        {
            case ModbusTcpClient tcp:
                tcp.Dispose();
                break;
            case ModbusRtuOverTcpClient rtuTcp:
                rtuTcp.Dispose();
                break;
        }
        _client = null;
        if (_bus is { } bus)
        {
            _bus = null;
            _buses.Release(bus, _deviceName);
        }
    }

    public Task<byte[]> ReadAsync(ModbusTable table, int start, int count, CancellationToken cancellationToken) =>
        RunAsync(async (client, ct) => table switch
        {
            ModbusTable.Coil => (await client.ReadCoilsAsync(Unit, start, count, ct)).ToArray(),
            ModbusTable.DiscreteInput => (await client.ReadDiscreteInputsAsync(Unit, start, count, ct)).ToArray(),
            ModbusTable.InputRegister => (await client.ReadInputRegistersAsync(Unit, (ushort)start, (ushort)count, ct)).ToArray(),
            _ => (await client.ReadHoldingRegistersAsync(Unit, (ushort)start, (ushort)count, ct)).ToArray(),
        }, cancellationToken);

    public Task WriteRegistersAsync(int start, byte[] data, CancellationToken cancellationToken) =>
        RunAsync(async (client, ct) =>
        {
            if (data.Length == 2 && _settings.UseSingleWriteFunctions)
                await client.WriteSingleRegisterAsync(Unit, (ushort)start, data, ct);
            else
                await client.WriteMultipleRegistersAsync(Unit, (ushort)start, data, ct);
            return true;
        }, cancellationToken);

    public Task WriteCoilAsync(int offset, bool value, CancellationToken cancellationToken) =>
        RunAsync(async (client, ct) =>
        {
            if (_settings.UseSingleWriteFunctions)
                await client.WriteSingleCoilAsync(Unit, offset, value, ct);
            else
                await client.WriteMultipleCoilsAsync(Unit, offset, [value], ct);
            return true;
        }, cancellationToken);

    public void Dispose() => Close();

    private async Task<T> RunAsync<T>(Func<ModbusClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            if (_bus is { } bus)
                return await bus.RunAsync(_settings.TimeoutMs, operation, cancellationToken);
            var client = _client ?? throw new IOException("Transporte cerrado.");
            return await operation(client, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"El esclavo {_settings.UnitId.ToString(CultureInfo.InvariantCulture)} no respondió en {_settings.TimeoutMs.ToString(CultureInfo.InvariantCulture)} ms" +
                (_settings.Transport == ModbusTransport.Tcp ? "." : ": revisa el unit id, el cableado y los ajustes serie (baudios, paridad)."), ex);
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            // FluentModbus avisa así de que el equipo cerró la conexión TCP.
            throw new IOException($"El equipo cerró la conexión: {ex.Message}", ex);
        }
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var ip))
            return ip;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
    }
}
