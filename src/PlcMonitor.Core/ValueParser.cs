using System.Globalization;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Core;

/// <summary>
/// Convierte texto introducido por el usuario (CLI, UI) al tipo .NET que corresponde a un <see cref="TagDataType"/>.
/// Usa siempre cultura invariante: el separador decimal es el punto.
/// </summary>
public static class ValueParser
{
    public static bool TryParse(string? text, TagDataType dataType, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (text is null)
        {
            error = "Valor vacío.";
            return false;
        }

        var s = text.Trim();
        var inv = CultureInfo.InvariantCulture;
        bool ok;
        switch (dataType)
        {
            case TagDataType.Boolean:
                ok = TryParseBool(s, out var b);
                value = b;
                break;
            case TagDataType.SByte:
                ok = sbyte.TryParse(s, NumberStyles.Integer, inv, out var sb);
                value = sb;
                break;
            case TagDataType.Byte:
                ok = TryParseUnsigned(s, byte.MaxValue, out var by);
                value = (byte)by;
                break;
            case TagDataType.Int16:
                ok = short.TryParse(s, NumberStyles.Integer, inv, out var i16);
                value = i16;
                break;
            case TagDataType.UInt16:
                ok = TryParseUnsigned(s, ushort.MaxValue, out var u16);
                value = (ushort)u16;
                break;
            case TagDataType.Int32:
                ok = int.TryParse(s, NumberStyles.Integer, inv, out var i32);
                value = i32;
                break;
            case TagDataType.UInt32:
                ok = TryParseUnsigned(s, uint.MaxValue, out var u32);
                value = (uint)u32;
                break;
            case TagDataType.Int64:
                ok = long.TryParse(s, NumberStyles.Integer, inv, out var i64);
                value = i64;
                break;
            case TagDataType.UInt64:
                ok = TryParseUnsigned(s, ulong.MaxValue, out var u64);
                value = u64;
                break;
            case TagDataType.Float:
                ok = float.TryParse(s, NumberStyles.Float, inv, out var f) && float.IsFinite(f);
                value = f;
                break;
            case TagDataType.Double:
                ok = double.TryParse(s, NumberStyles.Float, inv, out var d) && double.IsFinite(d);
                value = d;
                break;
            case TagDataType.String:
                ok = true;
                value = text;
                break;
            case TagDataType.DateTime:
                ok = DateTime.TryParse(s, inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt);
                value = dt;
                break;
            case TagDataType.TimeSpan:
                ok = TryParseTimeSpan(s, out var ts);
                value = ts;
                break;
            case TagDataType.ByteArray:
                ok = TryParseHex(s, out var bytes);
                value = bytes;
                break;
            default:
                error = $"No se puede escribir un valor de tipo {dataType}.";
                return false;
        }

        if (!ok)
        {
            value = null;
            error = $"'{text}' no es un valor válido para {dataType}.";
        }
        return ok;
    }

    private static bool TryParseBool(string s, out bool value)
    {
        switch (s.ToUpperInvariant())
        {
            case "1" or "TRUE" or "ON" or "SI" or "SÍ" or "YES":
                value = true;
                return true;
            case "0" or "FALSE" or "OFF" or "NO":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    /// <summary>Acepta decimal o hexadecimal con prefijo 0x / 16#.</summary>
    private static bool TryParseUnsigned(string s, ulong max, out ulong value)
    {
        string? hex = null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = s[2..];
        else if (s.StartsWith("16#", StringComparison.Ordinal))
            hex = s[3..];

        var ok = hex is not null
            ? ulong.TryParse(hex.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        return ok && value <= max;
    }

    /// <summary>Acepta "hh:mm:ss[.fff]" o milisegundos ("1500" o "1500ms").</summary>
    private static bool TryParseTimeSpan(string s, out TimeSpan value)
    {
        var ms = s.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? s[..^2] : s;
        if (long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
        {
            value = TimeSpan.FromMilliseconds(millis);
            return true;
        }
        return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseHex(string s, out byte[] value)
    {
        var clean = s.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        try
        {
            value = Convert.FromHexString(clean);
            return true;
        }
        catch (FormatException)
        {
            value = [];
            return false;
        }
    }
}
