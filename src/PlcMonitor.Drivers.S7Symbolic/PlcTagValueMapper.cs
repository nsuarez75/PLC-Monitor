using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using PlcMonitor.Abstractions;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>Traduce entre los tags de la librería S7CommPlus y los valores normalizados de PLC-Monitor.</summary>
internal static class PlcTagValueMapper
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> ValueProperties = new();

    /// <summary>Tipo normalizado según el tipo de dato S7 (softdatatype).</summary>
    public static TagDataType DataTypeOf(uint softdatatype) => softdatatype switch
    {
        Softdatatype.S7COMMP_SOFTDATATYPE_BOOL => TagDataType.Boolean,
        Softdatatype.S7COMMP_SOFTDATATYPE_BYTE or Softdatatype.S7COMMP_SOFTDATATYPE_USINT => TagDataType.Byte,
        Softdatatype.S7COMMP_SOFTDATATYPE_SINT => TagDataType.SByte,
        Softdatatype.S7COMMP_SOFTDATATYPE_WORD or Softdatatype.S7COMMP_SOFTDATATYPE_UINT => TagDataType.UInt16,
        Softdatatype.S7COMMP_SOFTDATATYPE_INT => TagDataType.Int16,
        Softdatatype.S7COMMP_SOFTDATATYPE_DWORD or Softdatatype.S7COMMP_SOFTDATATYPE_UDINT => TagDataType.UInt32,
        Softdatatype.S7COMMP_SOFTDATATYPE_DINT => TagDataType.Int32,
        Softdatatype.S7COMMP_SOFTDATATYPE_LWORD or Softdatatype.S7COMMP_SOFTDATATYPE_ULINT => TagDataType.UInt64,
        Softdatatype.S7COMMP_SOFTDATATYPE_LINT => TagDataType.Int64,
        Softdatatype.S7COMMP_SOFTDATATYPE_REAL => TagDataType.Float,
        Softdatatype.S7COMMP_SOFTDATATYPE_LREAL => TagDataType.Double,
        Softdatatype.S7COMMP_SOFTDATATYPE_CHAR or Softdatatype.S7COMMP_SOFTDATATYPE_WCHAR
            or Softdatatype.S7COMMP_SOFTDATATYPE_STRING or Softdatatype.S7COMMP_SOFTDATATYPE_WSTRING => TagDataType.String,
        Softdatatype.S7COMMP_SOFTDATATYPE_DATE or Softdatatype.S7COMMP_SOFTDATATYPE_DATEANDTIME
            or Softdatatype.S7COMMP_SOFTDATATYPE_DTL or Softdatatype.S7COMMP_SOFTDATATYPE_LDT => TagDataType.DateTime,
        Softdatatype.S7COMMP_SOFTDATATYPE_TIME or Softdatatype.S7COMMP_SOFTDATATYPE_LTIME
            or Softdatatype.S7COMMP_SOFTDATATYPE_TIMEOFDAY or Softdatatype.S7COMMP_SOFTDATATYPE_LTOD
            or Softdatatype.S7COMMP_SOFTDATATYPE_S5TIME => TagDataType.TimeSpan,
        _ => TagDataType.Unknown,
    };

    /// <summary>Nombre del tipo S7 (Int, Real, String...).</summary>
    public static string NativeTypeName(uint softdatatype) =>
        Softdatatype.Types.TryGetValue(softdatatype, out var name) ? name : $"0x{softdatatype:X}";

    /// <summary>Valor .NET y tipo normalizado del último valor leído en el tag.</summary>
    public static (object? Value, TagDataType DataType) GetValue(PlcTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        switch (tag)
        {
            case PlcTagS5Time s5:
                return (TimeSpan.FromMilliseconds(s5.TimeValue * S5TimeBaseMs(s5.TimeBase)), TagDataType.TimeSpan);
            case PlcTagTimeOfDay tod:
                return (TimeSpan.FromMilliseconds(tod.Value), TagDataType.TimeSpan);
            case PlcTagLTOD ltod:
                return (TimeSpan.FromTicks((long)(ltod.Value / 100)), TagDataType.TimeSpan);
            case PlcTagLTime ltime:
                return (TimeSpan.FromTicks(ltime.Value / 100), TagDataType.TimeSpan);
            case PlcTagLDT ldt:
                return (DateTime.UnixEpoch.AddTicks((long)(ldt.Value / 100)), TagDataType.DateTime);
            case PlcTagChar c:
                return (c.Value.ToString(), TagDataType.String);
            case PlcTagWChar wc:
                return (wc.Value.ToString(), TagDataType.String);
            default:
                break;
        }

        var raw = ValueProperty(tag.GetType())?.GetValue(tag);
        if (tag.GetType().Name == "PlcTagTime" && raw is int ms)
            return (TimeSpan.FromMilliseconds(ms), TagDataType.TimeSpan);

        return raw switch
        {
            null => (null, TagDataType.Unknown),
            byte[] bytes => (bytes, TagDataType.ByteArray),
            Array array => (array, ElementType(array.GetType().GetElementType()!)),
            _ => (raw, ElementType(raw.GetType())),
        };
    }

    /// <summary>Asigna <paramref name="value"/> al tag, convirtiéndolo a su tipo. Devuelve false con el motivo si no es posible.</summary>
    public static bool TrySetValue(PlcTag tag, object? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(tag);
        error = null;
        if (value is null)
        {
            error = "Valor nulo.";
            return false;
        }

        try
        {
            switch (tag)
            {
                case PlcTagS5Time:
                    error = "La escritura de S5TIME no está soportada.";
                    return false;
                case PlcTagTimeOfDay tod:
                    tod.Value = checked((uint)ToTimeSpan(value).TotalMilliseconds);
                    return true;
                case PlcTagLTOD ltod:
                    ltod.Value = checked((ulong)ToTimeSpan(value).Ticks * 100);
                    return true;
                case PlcTagLTime ltime:
                    ltime.Value = checked(ToTimeSpan(value).Ticks * 100);
                    return true;
                case PlcTagLDT ldt:
                    ldt.Value = checked((ulong)(ToUtc(value) - DateTime.UnixEpoch).Ticks * 100);
                    return true;
                case PlcTagChar c:
                    c.Value = ToChar(value);
                    return true;
                case PlcTagWChar wc:
                    wc.Value = ToChar(value);
                    return true;
                default:
                    break;
            }

            var property = ValueProperty(tag.GetType());
            if (property is null || !property.CanWrite)
            {
                error = $"El tipo {tag.GetType().Name} no se puede escribir.";
                return false;
            }

            var target = property.PropertyType;
            object converted;
            if (tag.GetType().Name == "PlcTagTime")
                converted = checked((int)ToTimeSpan(value).TotalMilliseconds);
            else if (target.IsArray)
                converted = ConvertArray(value, target.GetElementType()!);
            else if (target == typeof(DateTime))
                converted = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
            else
                converted = Convert.ChangeType(value, target, CultureInfo.InvariantCulture);

            property.SetValue(tag, converted);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            error = $"No se puede convertir '{value}' a {tag.GetType().Name.Replace("PlcTag", string.Empty, StringComparison.Ordinal)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Calidad normalizada a partir de la calidad OPC de la librería y el error de lectura.</summary>
    public static QualityCode QualityOf(PlcTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.LastReadError != 0)
            return QualityCode.BadAddressInvalid;
        return (tag.Quality & PlcTagQC.TAG_QUALITY_MASK) switch
        {
            PlcTagQC.TAG_QUALITY_GOOD => QualityCode.Good,
            PlcTagQC.TAG_QUALITY_UNCERTAIN => QualityCode.Uncertain,
            _ => QualityCode.Bad,
        };
    }

    private static PropertyInfo? ValueProperty(Type type) =>
        ValueProperties.GetOrAdd(type, static t => t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance));

    private static TagDataType ElementType(Type t) => Type.GetTypeCode(t) switch
    {
        TypeCode.Boolean => TagDataType.Boolean,
        TypeCode.Byte => TagDataType.Byte,
        TypeCode.SByte => TagDataType.SByte,
        TypeCode.Int16 => TagDataType.Int16,
        TypeCode.UInt16 => TagDataType.UInt16,
        TypeCode.Int32 => TagDataType.Int32,
        TypeCode.UInt32 => TagDataType.UInt32,
        TypeCode.Int64 => TagDataType.Int64,
        TypeCode.UInt64 => TagDataType.UInt64,
        TypeCode.Single => TagDataType.Float,
        TypeCode.Double => TagDataType.Double,
        TypeCode.String or TypeCode.Char => TagDataType.String,
        TypeCode.DateTime => TagDataType.DateTime,
        _ => TagDataType.Unknown,
    };

    private static int S5TimeBaseMs(ushort timeBase) => timeBase switch
    {
        0 => 10,
        1 => 100,
        2 => 1000,
        _ => 10000,
    };

    private static TimeSpan ToTimeSpan(object value) => value switch
    {
        TimeSpan ts => ts,
        string s when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => TimeSpan.FromMilliseconds(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
    };

    private static DateTime ToUtc(object value)
    {
        var dt = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        return dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    private static char ToChar(object value)
    {
        var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return s.Length == 1 ? s[0] : throw new ArgumentException("Se esperaba un único carácter.");
    }

    private static Array ConvertArray(object value, Type elementType)
    {
        if (value is not Array source)
            throw new ArgumentException("Se esperaba un array.");
        var result = Array.CreateInstance(elementType, source.Length);
        for (var i = 0; i < source.Length; i++)
            result.SetValue(Convert.ChangeType(source.GetValue(i), elementType, CultureInfo.InvariantCulture), i);
        return result;
    }
}
