using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PlcMonitor.Drivers.S7Absolute;

/// <summary>Conversión entre bytes S7 (big-endian) y valores .NET.</summary>
public static class S7Codec
{
    // Los S7 usan una página de códigos de 8 bits para STRING/CHAR; Latin-1 es la equivalencia habitual.
    private static readonly Encoding Text = Encoding.Latin1;

    public static object Decode(S7Address address, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (data.Length < address.ByteLength)
            throw new ArgumentException($"Se esperaban {address.ByteLength} bytes para {address} y hay {data.Length}.", nameof(data));

        if (address.ValueType == S7ValueType.Byte && address.ArrayCount > 1)
            return data[..address.ByteLength].ToArray();

        if (address.ArrayCount > 1)
        {
            var size = address.ElementSize;
            var array = Array.CreateInstance(ElementClrType(address.ValueType), address.ArrayCount);
            for (var i = 0; i < address.ArrayCount; i++)
                array.SetValue(DecodeElement(address, data.Slice(i * size, size)), i);
            return array;
        }
        return DecodeElement(address, data);
    }

    public static byte[] Encode(S7Address address, object? value)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(value);

        if (address.ValueType == S7ValueType.Bit)
            throw new InvalidOperationException("Los bits se escriben individualmente, no como bytes.");

        if (address.ArrayCount > 1)
        {
            if (address.ValueType == S7ValueType.Byte && value is byte[] raw)
            {
                CheckLength(address, raw.Length);
                return raw;
            }
            if (value is not Array array)
                throw new ArgumentException($"{address} es un array de {address.ArrayCount} elementos; se esperaba un array.");
            CheckLength(address, array.Length);
            var buffer = new byte[address.ByteLength];
            for (var i = 0; i < array.Length; i++)
                EncodeElement(address, array.GetValue(i), buffer.AsSpan(i * address.ElementSize, address.ElementSize));
            return buffer;
        }

        var result = new byte[address.ElementSize];
        EncodeElement(address, value, result);
        return result;
    }

    private static void CheckLength(S7Address address, int length)
    {
        if (length != address.ArrayCount)
            throw new ArgumentException($"{address} tiene {address.ArrayCount} elementos y se han dado {length}.");
    }

    private static Type ElementClrType(S7ValueType type) => type switch
    {
        S7ValueType.Byte => typeof(byte),
        S7ValueType.Word => typeof(ushort),
        S7ValueType.Int => typeof(short),
        S7ValueType.DWord => typeof(uint),
        S7ValueType.DInt => typeof(int),
        S7ValueType.Real => typeof(float),
        S7ValueType.LWord => typeof(ulong),
        S7ValueType.LInt => typeof(long),
        S7ValueType.LReal => typeof(double),
        _ => typeof(object),
    };

    private static object DecodeElement(S7Address a, ReadOnlySpan<byte> d) => a.ValueType switch
    {
        S7ValueType.Bit => ((d[0] >> a.BitIndex) & 1) == 1,
        S7ValueType.Byte => d[0],
        S7ValueType.Char => Text.GetString(d[..1]),
        S7ValueType.Word => BinaryPrimitives.ReadUInt16BigEndian(d),
        S7ValueType.Int => BinaryPrimitives.ReadInt16BigEndian(d),
        S7ValueType.DWord => BinaryPrimitives.ReadUInt32BigEndian(d),
        S7ValueType.DInt => BinaryPrimitives.ReadInt32BigEndian(d),
        S7ValueType.Real => BinaryPrimitives.ReadSingleBigEndian(d),
        S7ValueType.LWord => BinaryPrimitives.ReadUInt64BigEndian(d),
        S7ValueType.LInt => BinaryPrimitives.ReadInt64BigEndian(d),
        S7ValueType.LReal => BinaryPrimitives.ReadDoubleBigEndian(d),
        S7ValueType.String => DecodeString(a, d),
        S7ValueType.DateAndTime => DecodeDateAndTime(d),
        _ => throw new NotSupportedException(a.ValueType.ToString()),
    };

    private static string DecodeString(S7Address a, ReadOnlySpan<byte> d)
    {
        // Byte 0: longitud máxima, byte 1: longitud actual, después los caracteres.
        var length = Math.Min(Math.Min(d[1], d[0] == 0 ? a.StringLength : d[0]), a.StringLength);
        return Text.GetString(d.Slice(2, length));
    }

    private static void EncodeElement(S7Address a, object? value, Span<byte> d)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (a.ValueType)
        {
            case S7ValueType.Byte: d[0] = Convert.ToByte(value, inv); break;
            case S7ValueType.Char: EncodeChar(value, d); break;
            case S7ValueType.Word: BinaryPrimitives.WriteUInt16BigEndian(d, Convert.ToUInt16(value, inv)); break;
            case S7ValueType.Int: BinaryPrimitives.WriteInt16BigEndian(d, Convert.ToInt16(value, inv)); break;
            case S7ValueType.DWord: BinaryPrimitives.WriteUInt32BigEndian(d, Convert.ToUInt32(value, inv)); break;
            case S7ValueType.DInt: BinaryPrimitives.WriteInt32BigEndian(d, Convert.ToInt32(value, inv)); break;
            case S7ValueType.Real: BinaryPrimitives.WriteSingleBigEndian(d, Convert.ToSingle(value, inv)); break;
            case S7ValueType.LWord: BinaryPrimitives.WriteUInt64BigEndian(d, Convert.ToUInt64(value, inv)); break;
            case S7ValueType.LInt: BinaryPrimitives.WriteInt64BigEndian(d, Convert.ToInt64(value, inv)); break;
            case S7ValueType.LReal: BinaryPrimitives.WriteDoubleBigEndian(d, Convert.ToDouble(value, inv)); break;
            case S7ValueType.String: EncodeString(a, Convert.ToString(value, inv) ?? string.Empty, d); break;
            case S7ValueType.DateAndTime: EncodeDateAndTime(Convert.ToDateTime(value, inv), d); break;
            default: throw new NotSupportedException(a.ValueType.ToString());
        }
    }

    private static void EncodeChar(object? value, Span<byte> d)
    {
        var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (s.Length != 1)
            throw new ArgumentException("Un CHAR debe ser exactamente un carácter.");
        d[0] = Text.GetBytes(s)[0];
    }

    private static void EncodeString(S7Address a, string s, Span<byte> d)
    {
        var bytes = Text.GetBytes(s);
        if (bytes.Length > a.StringLength)
            throw new ArgumentException($"El texto tiene {bytes.Length} caracteres y el STRING admite {a.StringLength}.");
        d[0] = (byte)a.StringLength;
        d[1] = (byte)bytes.Length;
        bytes.CopyTo(d[2..]);
    }

    // DATE_AND_TIME: 8 bytes BCD = año(2 dígitos) mes día hora min seg ms(3 dígitos) díaSemana(1 dígito).
    private static DateTime DecodeDateAndTime(ReadOnlySpan<byte> d)
    {
        var yy = Bcd(d[0]);
        var year = yy >= 90 ? 1900 + yy : 2000 + yy;
        var ms = (Bcd(d[6]) * 10) + (d[7] >> 4);
        return new DateTime(year, Bcd(d[1]), Bcd(d[2]), Bcd(d[3]), Bcd(d[4]), Bcd(d[5]), ms, DateTimeKind.Unspecified);
    }

    private static void EncodeDateAndTime(DateTime t, Span<byte> d)
    {
        if (t.Year is < 1990 or > 2089)
            throw new ArgumentOutOfRangeException(nameof(t), "DATE_AND_TIME admite años de 1990 a 2089.");
        d[0] = ToBcd(t.Year % 100);
        d[1] = ToBcd(t.Month);
        d[2] = ToBcd(t.Day);
        d[3] = ToBcd(t.Hour);
        d[4] = ToBcd(t.Minute);
        d[5] = ToBcd(t.Second);
        d[6] = ToBcd(t.Millisecond / 10);
        var weekday = (int)t.DayOfWeek + 1; // S7: 1 = domingo
        d[7] = (byte)(((t.Millisecond % 10) << 4) | weekday);
    }

    private static int Bcd(byte b) => ((b >> 4) * 10) + (b & 0x0F);

    private static byte ToBcd(int v) => (byte)(((v / 10) << 4) | (v % 10));
}
