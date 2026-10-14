namespace PlcMonitor.Drivers.S7Absolute;

public enum S7CpuFamily
{
    S7300,
    S7400,
    S71200,
    S71500,
}

/// <summary>Ajustes del driver S7 absoluto (PUT/GET).</summary>
public sealed class S7AbsoluteSettings
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 102;

    public S7CpuFamily Cpu { get; set; } = S7CpuFamily.S71500;

    /// <summary>Rack de la CPU (normalmente 0).</summary>
    public short Rack { get; set; }

    /// <summary>Slot de la CPU: 2 en S7-300, 1 en S7-1500, 0 o 1 en S7-1200.</summary>
    public short Slot { get; set; } = 1;

    public int TimeoutMs { get; set; } = 5000;

    /// <summary>Hueco máximo (bytes) entre variables para leerlas en la misma petición.</summary>
    public int ReadGapBytes { get; set; } = 32;
}
