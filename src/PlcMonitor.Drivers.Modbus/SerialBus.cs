using System.IO.Ports;
using FluentModbus;

namespace PlcMonitor.Drivers.Modbus;

/// <summary>Puerto serie para el cliente RTU, con timeout por petición (cada esclavo del bus puede tener el suyo).</summary>
internal interface IRtuSerialPort : IModbusRtuSerialPort, IDisposable
{
    int TimeoutMs { get; set; }

    /// <summary>Descarta lo recibido y no leído (p.ej. la respuesta tardía de un esclavo al que ya se dio por perdido).</summary>
    void DiscardInBuffer();
}

/// <summary>
/// Un puerto serie con uno o varios esclavos (RS-485). Cada esclavo es un dispositivo del agente con su propia conexión;
/// todos comparten el cliente RTU de su bus y se turnan: una petición cada vez y un silencio entre tramas.
/// </summary>
internal sealed class SerialBus : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ModbusRtuClient _client = new();
    private readonly IRtuSerialPort _port;
    private readonly TimeSpan _silence;

    public SerialBus(IRtuSerialPort port, ModbusSettings settings)
    {
        _port = port;
        PortName = settings.SerialPort;
        Signature = SignatureOf(settings);
        _silence = SilenceOf(settings.BaudRate);
        _client.Initialize(port, ModbusEndianness.BigEndian);
    }

    public string PortName { get; }

    /// <summary>Ajustes de línea del bus: todos los esclavos de un mismo puerto deben usar los mismos.</summary>
    public string Signature { get; }

    /// <summary>Dispositivos que usan el bus.</summary>
    public HashSet<string> Devices { get; } = [];

    public bool IsOpen => _port.IsOpen;

    public async Task<T> RunAsync<T>(int timeoutMs, Func<ModbusClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _port.TimeoutMs = timeoutMs;
            _port.DiscardInBuffer();
            return await operation(_client, cancellationToken);
        }
        finally
        {
            // Silencio entre tramas (3,5 caracteres) antes de dejar paso al siguiente esclavo.
            await Task.Delay(_silence, CancellationToken.None);
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _port.Close();
        _port.Dispose();
        _gate.Dispose();
    }

    public static string SignatureOf(ModbusSettings settings) =>
        FormattableString.Invariant($"{settings.BaudRate} baudios, paridad {settings.Parity}, {(settings.StopBits == ModbusStopBits.Two ? 2 : 1)} bit(s) de parada");

    /// <summary>3,5 caracteres de 11 bits; por encima de 19200 baudios la norma lo fija en 1,75 ms.</summary>
    internal static TimeSpan SilenceOf(int baudRate) =>
        baudRate > 19200 ? TimeSpan.FromMilliseconds(1.75) : TimeSpan.FromMilliseconds(Math.Ceiling(3.5 * 11 * 1000 / baudRate));
}

/// <summary>
/// Buses serie abiertos en el proceso, por nombre de puerto. El primer dispositivo de un puerto lo abre y el último lo
/// cierra; los demás se suman al bus abierto si sus ajustes de línea coinciden.
/// </summary>
internal sealed class SerialBusRegistry
{
    private readonly Func<ModbusSettings, IRtuSerialPort> _portFactory;
    private readonly Dictionary<string, SerialBus> _buses =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public SerialBusRegistry(Func<ModbusSettings, IRtuSerialPort>? portFactory = null)
    {
        _portFactory = portFactory ?? (s => new SystemRtuSerialPort(s));
    }

    public static SerialBusRegistry Shared { get; } = new();

    public SerialBus Acquire(ModbusSettings settings, string deviceName)
    {
        lock (_lock)
        {
            if (_buses.TryGetValue(settings.SerialPort, out var bus))
            {
                if (bus.Signature != SerialBus.SignatureOf(settings))
                {
                    throw new InvalidOperationException(
                        $"El puerto {settings.SerialPort} ya lo usa(n) {string.Join(", ", bus.Devices.Order(StringComparer.Ordinal))} con " +
                        $"{bus.Signature}; todos los esclavos de un mismo bus deben tener los mismos ajustes serie.");
                }
                bus.Devices.Add(deviceName);
                return bus;
            }

            var port = _portFactory(settings);
            port.Open();
            bus = new SerialBus(port, settings);
            bus.Devices.Add(deviceName);
            _buses[settings.SerialPort] = bus;
            return bus;
        }
    }

    public void Release(SerialBus bus, string deviceName)
    {
        lock (_lock)
        {
            bus.Devices.Remove(deviceName);
            if (bus.Devices.Count > 0)
                return;
            _buses.Remove(bus.PortName);
            bus.Dispose();
        }
    }

    /// <summary>Puertos abiertos (para los tests).</summary>
    internal int OpenCount
    {
        get
        {
            lock (_lock)
                return _buses.Count;
        }
    }
}

/// <summary>Puerto serie real (System.IO.Ports): COM3 en Windows, /dev/ttyUSB0 en Linux. Siempre 8 bits de datos (RTU).</summary>
internal sealed class SystemRtuSerialPort : IRtuSerialPort
{
    private readonly SerialPort _port;

    public SystemRtuSerialPort(ModbusSettings settings)
    {
        _port = new SerialPort(settings.SerialPort)
        {
            BaudRate = settings.BaudRate,
            DataBits = 8,
            Parity = settings.Parity switch
            {
                ModbusParity.Even => Parity.Even,
                ModbusParity.Odd => Parity.Odd,
                _ => Parity.None,
            },
            StopBits = settings.StopBits == ModbusStopBits.Two ? StopBits.Two : StopBits.One,
            Handshake = Handshake.None,
            ReadTimeout = settings.TimeoutMs,
            WriteTimeout = settings.TimeoutMs,
        };
    }

    public string PortName => _port.PortName;

    public bool IsOpen => _port.IsOpen;

    public int TimeoutMs { get; set; } = 1000;

    public void Open() => _port.Open();

    public void Close() => _port.Close();

    public void Dispose() => _port.Dispose();

    public void DiscardInBuffer()
    {
        if (_port.IsOpen)
            _port.DiscardInBuffer();
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        _port.ReadTimeout = TimeoutMs;
        return _port.Read(buffer, offset, count);
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        _port.WriteTimeout = TimeoutMs;
        _port.Write(buffer, offset, count);
    }

    // Como ModbusRtuSerialPort de FluentModbus: BaseStream no respeta ReadTimeout en asíncrono, así que se cancela a mano
    // (y DiscardInBuffer desbloquea la lectura pendiente).
    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        WithTimeoutAsync(t => _port.BaseStream.ReadAsync(buffer, offset, count, t), DiscardInBuffer, token);

    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        WithTimeoutAsync(async t =>
        {
            await _port.BaseStream.WriteAsync(buffer.AsMemory(offset, count), t);
            return 0;
        }, () => _port.DiscardOutBuffer(), token);

    private async Task<int> WithTimeoutAsync(Func<CancellationToken, Task<int>> operation, Action unblock, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
        using var _ = timeout.Token.Register(() =>
        {
            if (_port.IsOpen)
                unblock();
        });
        try
        {
            return await operation(linked.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Sin respuesta por {_port.PortName} en {TimeoutMs} ms.", ex);
        }
    }
}
