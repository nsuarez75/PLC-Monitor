using S7.Net;

namespace PlcMonitor.Drivers.S7Absolute;

/// <summary>Acceso de bajo nivel al PLC (bytes). Permite sustituir S7.Net en los tests.</summary>
internal interface IS7Transport : IDisposable
{
    bool IsConnected { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    void Close();

    Task<byte[]> ReadBytesAsync(S7Area area, int db, int start, int count, CancellationToken cancellationToken);

    Task WriteBytesAsync(S7Area area, int db, int start, byte[] data, CancellationToken cancellationToken);

    Task WriteBitAsync(S7Area area, int db, int start, int bit, bool value, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetInfoAsync(CancellationToken cancellationToken);
}

/// <summary>Transporte real basado en S7.Net Plus (MIT).</summary>
internal sealed class S7NetTransport : IS7Transport
{
    private readonly Plc _plc;
    private readonly S7AbsoluteSettings _settings;

    public S7NetTransport(S7AbsoluteSettings settings)
    {
        _settings = settings;
        var cpu = settings.Cpu switch
        {
            S7CpuFamily.S7300 => CpuType.S7300,
            S7CpuFamily.S7400 => CpuType.S7400,
            S7CpuFamily.S71200 => CpuType.S71200,
            _ => CpuType.S71500,
        };
        _plc = new Plc(cpu, settings.Host, settings.Port, settings.Rack, settings.Slot)
        {
            ReadTimeout = settings.TimeoutMs,
            WriteTimeout = settings.TimeoutMs,
        };
    }

    public bool IsConnected => _plc.IsConnected;

    public Task OpenAsync(CancellationToken cancellationToken) => _plc.OpenAsync(cancellationToken);

    public void Close() => _plc.Close();

    public Task<byte[]> ReadBytesAsync(S7Area area, int db, int start, int count, CancellationToken cancellationToken) =>
        _plc.ReadBytesAsync(Map(area), db, start, count, cancellationToken);

    public Task WriteBytesAsync(S7Area area, int db, int start, byte[] data, CancellationToken cancellationToken) =>
        _plc.WriteBytesAsync(Map(area), db, start, data, cancellationToken);

    public Task WriteBitAsync(S7Area area, int db, int start, int bit, bool value, CancellationToken cancellationToken) =>
        _plc.WriteBitAsync(Map(area), db, start, bit, value, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetInfoAsync(CancellationToken cancellationToken)
    {
        var info = new Dictionary<string, string>
        {
            ["Cpu"] = _settings.Cpu.ToString(),
            ["Rack/Slot"] = $"{_settings.Rack}/{_settings.Slot}",
            ["PduSize"] = _plc.MaxPDUSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        try
        {
            var status = await _plc.ReadStatusAsync(cancellationToken);
            info["OperatingState"] = status switch
            {
                0x08 => "RUN",
                0x04 => "STOP",
                _ => $"0x{status:X2}",
            };
        }
#pragma warning disable CA1031 // El estado es informativo: si la CPU no lo da, se omite.
        catch (Exception)
#pragma warning restore CA1031
        {
            info["OperatingState"] = "desconocido";
        }
        return info;
    }

    public void Dispose() => _plc.Close();

    private static DataType Map(S7Area area) => area switch
    {
        S7Area.Inputs => DataType.Input,
        S7Area.Outputs => DataType.Output,
        S7Area.Merker => DataType.Memory,
        _ => DataType.DataBlock,
    };
}
