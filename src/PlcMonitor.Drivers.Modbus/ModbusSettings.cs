namespace PlcMonitor.Drivers.Modbus;

/// <summary>Cómo llegan las tramas Modbus al equipo.</summary>
public enum ModbusTransport
{
    /// <summary>Modbus TCP (puerto 502).</summary>
    Tcp,

    /// <summary>Modbus RTU por un puerto serie del equipo del agente (RS-485/RS-232).</summary>
    Rtu,

    /// <summary>Tramas RTU dentro de una conexión TCP (convertidores serie-Ethernet en modo transparente).</summary>
    RtuOverTcp,
}

/// <summary>
/// Orden de los bytes de los valores de varios registros. A es el byte más significativo:
/// ABCD = tal cual (big-endian), CDAB = palabras intercambiadas, BADC = bytes intercambiados en cada registro, DCBA = ambos.
/// </summary>
public enum ModbusByteOrder
{
    ABCD,
    BADC,
    CDAB,
    DCBA,
}

public enum ModbusParity
{
    None,
    Even,
    Odd,
}

public enum ModbusStopBits
{
    One,
    Two,
}

/// <summary>Ajustes del driver Modbus.</summary>
public sealed class ModbusSettings
{
    public ModbusTransport Transport { get; set; } = ModbusTransport.Tcp;

    /// <summary>Equipo (TCP o pasarela RTU sobre TCP).</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = 502;

    /// <summary>Puerto serie del equipo del agente (COM3, /dev/ttyUSB0...). Solo RTU.</summary>
    public string SerialPort { get; set; } = "";

    public int BaudRate { get; set; } = 9600;

    public ModbusParity Parity { get; set; } = ModbusParity.None;

    public ModbusStopBits StopBits { get; set; } = ModbusStopBits.One;

    /// <summary>Número de esclavo (unit id). En Modbus TCP directo suele valer 1 o 255; detrás de una pasarela, el del esclavo serie.</summary>
    public int UnitId { get; set; } = 1;

    public int TimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Las direcciones por tabla (HR1, C1...) empiezan en 1 en vez de en 0. No afecta a las de estilo Modicon (40001), que
    /// siempre empiezan en 1.
    /// </summary>
    public bool OneBasedAddresses { get; set; }

    /// <summary>Orden de bytes de los valores de 32 y 64 bits (y de los textos); cada tag puede indicar otro en su dirección.</summary>
    public ModbusByteOrder ByteOrder { get; set; } = ModbusByteOrder.ABCD;

    /// <summary>Registros por petición (el protocolo admite hasta 125; algunos equipos, menos).</summary>
    public int MaxRegistersPerRead { get; set; } = 125;

    /// <summary>Bits (coils/entradas) por petición (el protocolo admite hasta 2000).</summary>
    public int MaxBitsPerRead { get; set; } = 2000;

    /// <summary>Hueco máximo (registros o bits) entre tags para leerlos en la misma petición.</summary>
    public int ReadGap { get; set; } = 8;

    /// <summary>
    /// Para escribir un solo coil o registro se usan las funciones 05/06. Desactivado: siempre 15/16 (hay equipos que solo
    /// admiten estas).
    /// </summary>
    public bool UseSingleWriteFunctions { get; set; } = true;
}
