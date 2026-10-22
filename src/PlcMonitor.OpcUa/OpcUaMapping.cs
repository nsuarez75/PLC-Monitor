using System.Globalization;
using Opc.Ua;
using PlcMonitor.Abstractions;
using PlcMonitor.Historian;

namespace PlcMonitor.OpcUa;

/// <summary>Conversión entre los tipos, valores y calidades del motor y los de OPC UA.</summary>
internal static class OpcUaMapping
{
    /// <summary>Tipo OPC UA de un tag. Sin tipo conocido, BaseDataType (cualquier valor).</summary>
    public static NodeId DataTypeOf(TagDataType? dataType, bool scaled)
    {
        if (scaled)
            return DataTypeIds.Double;
        return dataType switch
        {
            TagDataType.Boolean => DataTypeIds.Boolean,
            TagDataType.SByte => DataTypeIds.SByte,
            TagDataType.Byte => DataTypeIds.Byte,
            TagDataType.Int16 => DataTypeIds.Int16,
            TagDataType.UInt16 => DataTypeIds.UInt16,
            TagDataType.Int32 => DataTypeIds.Int32,
            TagDataType.UInt32 => DataTypeIds.UInt32,
            TagDataType.Int64 => DataTypeIds.Int64,
            TagDataType.UInt64 => DataTypeIds.UInt64,
            TagDataType.Float => DataTypeIds.Float,
            TagDataType.Double => DataTypeIds.Double,
            TagDataType.String => DataTypeIds.String,
            TagDataType.DateTime => DataTypeIds.DateTime,
            // Duración en milisegundos (tipo Duration de OPC UA).
            TagDataType.TimeSpan => DataTypeIds.Duration,
            TagDataType.ByteArray => DataTypeIds.ByteString,
            _ => DataTypeIds.BaseDataType,
        };
    }

    /// <summary>
    /// Valor del motor como Variant del tipo de la variable: si el driver da otro tipo numérico (p.ej. un short en un tag
    /// Int32), se convierte; los clientes OPC UA esperan exactamente el tipo declarado.
    /// </summary>
    public static Variant ToVariant(object? value, NodeId dataType)
    {
        if (value is null or string or bool || !value.GetType().IsPrimitive)
            return ToVariant(value);
        try
        {
            var c = CultureInfo.InvariantCulture;
            if (dataType == DataTypeIds.Boolean)
                return Variant.From(Convert.ToBoolean(value, c));
            if (dataType == DataTypeIds.SByte)
                return Variant.From(Convert.ToSByte(value, c));
            if (dataType == DataTypeIds.Byte)
                return Variant.From(Convert.ToByte(value, c));
            if (dataType == DataTypeIds.Int16)
                return Variant.From(Convert.ToInt16(value, c));
            if (dataType == DataTypeIds.UInt16)
                return Variant.From(Convert.ToUInt16(value, c));
            if (dataType == DataTypeIds.Int32)
                return Variant.From(Convert.ToInt32(value, c));
            if (dataType == DataTypeIds.UInt32)
                return Variant.From(Convert.ToUInt32(value, c));
            if (dataType == DataTypeIds.Int64)
                return Variant.From(Convert.ToInt64(value, c));
            if (dataType == DataTypeIds.UInt64)
                return Variant.From(Convert.ToUInt64(value, c));
            if (dataType == DataTypeIds.Float)
                return Variant.From(Convert.ToSingle(value, c));
            if (dataType == DataTypeIds.Double || dataType == DataTypeIds.Duration)
                return Variant.From(Convert.ToDouble(value, c));
        }
        catch (OverflowException)
        {
            // Fuera de rango para el tipo declarado: se publica tal cual.
        }
        return ToVariant(value);
    }

    /// <summary>Valor del motor como Variant de su propio tipo (null = Variant nulo).</summary>
    public static Variant ToVariant(object? value) => value switch
    {
        null => Variant.Null,
        bool b => Variant.From(b),
        sbyte v => Variant.From(v),
        byte v => Variant.From(v),
        short v => Variant.From(v),
        ushort v => Variant.From(v),
        int v => Variant.From(v),
        uint v => Variant.From(v),
        long v => Variant.From(v),
        ulong v => Variant.From(v),
        float v => Variant.From(v),
        double v => Variant.From(v),
        string s => Variant.From(s),
        DateTimeOffset d => Variant.From((DateTimeUtc)d.UtcDateTime),
        DateTime d => Variant.From((DateTimeUtc)d.ToUniversalTime()),
        TimeSpan t => Variant.From(t.TotalMilliseconds),
        byte[] bytes => Variant.From(ByteString.From(bytes)),
        IFormattable f => Variant.From(f.ToString(null, CultureInfo.InvariantCulture)),
        _ => Variant.From(value.ToString() ?? ""),
    };

    /// <summary>
    /// Punto del histórico como valor del tipo de la variable (el histórico guarda números en double y textos aparte).
    /// </summary>
    public static Variant ToVariant(HistoryPoint point, NodeId dataType)
    {
        if (point.Text is not null)
            return Variant.From(point.Text);
        if (point.Number is not { } n)
            return Variant.Null;
        if (dataType == DataTypeIds.Boolean)
            return Variant.From(n != 0);
        if (dataType == DataTypeIds.SByte)
            return Variant.From((sbyte)n);
        if (dataType == DataTypeIds.Byte)
            return Variant.From((byte)n);
        if (dataType == DataTypeIds.Int16)
            return Variant.From((short)n);
        if (dataType == DataTypeIds.UInt16)
            return Variant.From((ushort)n);
        if (dataType == DataTypeIds.Int32)
            return Variant.From((int)n);
        if (dataType == DataTypeIds.UInt32)
            return Variant.From((uint)n);
        if (dataType == DataTypeIds.Int64)
            return Variant.From((long)n);
        if (dataType == DataTypeIds.UInt64)
            return Variant.From((ulong)n);
        if (dataType == DataTypeIds.Float)
            return Variant.From((float)n);
        return Variant.From(n);
    }

    public static StatusCode ToStatus(QualityCode quality) => quality switch
    {
        QualityCode.Good => StatusCodes.Good,
        QualityCode.Uncertain => StatusCodes.Uncertain,
        QualityCode.BadNotConnected => StatusCodes.BadNotConnected,
        // La dirección no existe en el PLC: es un error de configuración del tag (BadNodeIdUnknown significaría que el nodo
        // OPC UA no existe).
        QualityCode.BadAddressInvalid => StatusCodes.BadConfigurationError,
        QualityCode.BadAccessDenied => StatusCodes.BadUserAccessDenied,
        QualityCode.BadTypeMismatch => StatusCodes.BadTypeMismatch,
        QualityCode.BadTimeout => StatusCodes.BadTimeout,
        QualityCode.BadConfiguration => StatusCodes.BadConfigurationError,
        QualityCode.BadNotSupported => StatusCodes.BadNotSupported,
        QualityCode.BadWriteDisabled => StatusCodes.BadNotWritable,
        _ => StatusCodes.Bad,
    };

    /// <summary>Valor que llega en una escritura OPC UA, como texto para el motor (que lo convierte al tipo del tag).</summary>
    public static string ToWriteText(Variant value) => value.AsBoxedObject() switch
    {
        null => "",
        bool b => b ? "true" : "false",
        DateTimeUtc d => ((DateTime)d).ToString("O", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        ByteString bytes => Convert.ToHexString(bytes.ToArray()),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString() ?? "",
    };
}
