namespace PlcMonitor.Abstractions;

/// <summary>Tipo de dato normalizado, independiente del protocolo.</summary>
public enum TagDataType
{
    Unknown = 0,
    Boolean,
    SByte,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    String,
    DateTime,
    TimeSpan,
    ByteArray,
}
