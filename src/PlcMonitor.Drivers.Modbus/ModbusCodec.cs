using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PlcMonitor.Drivers.Modbus;

/// <summary>
/// Conversión entre registros Modbus y valores .NET. Los registros llegan como bytes en orden de cable (cada registro, byte
/// alto primero); el orden de bytes del tag o del dispositivo dice cómo se reparten los bytes del valor entre ellos.
/// </summary>
public static class ModbusCodec
{
    // Los textos en registros suelen ser ASCII; Latin-1 conserva además los acentos de los equipos europeos.
    private static readonly Encoding Text = Encoding.Latin1;

    /// <summary>Decodifica un valor de registros. <paramref name="registers"/> son los bytes de sus <see cref="ModbusAddress.Count"/> registros.</summary>
    public static object Decode(ModbusAddress address, ReadOnlySpan<byte> registers, ModbusByteOrder deviceOrder)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsBitTable)
            throw new InvalidOperationException("Los coils y las entradas discretas se decodifican como bits.");
        var length = address.Count * 2;
        if (registers.Length < length)
            throw new ArgumentException($"Se esperaban {address.Count} registros para {address} y hay {registers.Length / 2}.", nameof(registers));

        if (address.IsRegisterBit)
            return ((BinaryPrimitives.ReadUInt16BigEndian(registers) >> address.BitIndex) & 1) == 1;

        Span<byte> d = stackalloc byte[length];
        registers[..length].CopyTo(d);
        Normalize(address, d, address.ByteOrder ?? deviceOrder);
        return address.ValueType switch
        {
            ModbusValueType.Int16 => BinaryPrimitives.ReadInt16BigEndian(d),
            ModbusValueType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(d),
            ModbusValueType.Int32 => BinaryPrimitives.ReadInt32BigEndian(d),
            ModbusValueType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(d),
            ModbusValueType.Int64 => BinaryPrimitives.ReadInt64BigEndian(d),
            ModbusValueType.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(d),
            ModbusValueType.Float32 => BinaryPrimitives.ReadSingleBigEndian(d),
            ModbusValueType.Float64 => BinaryPrimitives.ReadDoubleBigEndian(d),
            ModbusValueType.String => DecodeString(address, d),
            _ => throw new NotSupportedException(address.ValueType.ToString()),
        };
    }

    /// <summary>Codifica un valor en los bytes de sus registros (orden de cable). No vale para bits (coils o bits de registro).</summary>
    public static byte[] Encode(ModbusAddress address, object? value, ModbusByteOrder deviceOrder)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(value);
        if (address.IsBitTable || address.IsRegisterBit)
            throw new InvalidOperationException("Los bits se escriben aparte, no como registros.");

        var inv = CultureInfo.InvariantCulture;
        var d = new byte[address.Count * 2];
        switch (address.ValueType)
        {
            case ModbusValueType.Int16: BinaryPrimitives.WriteInt16BigEndian(d, Convert.ToInt16(value, inv)); break;
            case ModbusValueType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(d, Convert.ToUInt16(value, inv)); break;
            case ModbusValueType.Int32: BinaryPrimitives.WriteInt32BigEndian(d, Convert.ToInt32(value, inv)); break;
            case ModbusValueType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(d, Convert.ToUInt32(value, inv)); break;
            case ModbusValueType.Int64: BinaryPrimitives.WriteInt64BigEndian(d, Convert.ToInt64(value, inv)); break;
            case ModbusValueType.UInt64: BinaryPrimitives.WriteUInt64BigEndian(d, Convert.ToUInt64(value, inv)); break;
            case ModbusValueType.Float32: BinaryPrimitives.WriteSingleBigEndian(d, Convert.ToSingle(value, inv)); break;
            case ModbusValueType.Float64: BinaryPrimitives.WriteDoubleBigEndian(d, Convert.ToDouble(value, inv)); break;
            case ModbusValueType.String: EncodeString(address, Convert.ToString(value, inv) ?? string.Empty, d); break;
            default: throw new NotSupportedException(address.ValueType.ToString());
        }
        // Las transformaciones son su propia inversa: la misma operación pasa del valor a los registros.
        Normalize(address, d, address.ByteOrder ?? deviceOrder);
        return d;
    }

    /// <summary>Cambia un bit de un registro (bytes en orden de cable) y devuelve el registro resultante.</summary>
    public static byte[] SetRegisterBit(ReadOnlySpan<byte> register, int bit, bool value)
    {
        var word = BinaryPrimitives.ReadUInt16BigEndian(register);
        word = value ? (ushort)(word | (1 << bit)) : (ushort)(word & ~(1 << bit));
        var result = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(result, word);
        return result;
    }

    /// <summary>Lee el bit <paramref name="index"/> de una respuesta de coils/entradas (empaquetados, el primero en el bit 0).</summary>
    public static bool GetBit(ReadOnlySpan<byte> packed, int index) => ((packed[index / 8] >> (index % 8)) & 1) == 1;

    /// <summary>
    /// Pasa de los registros al orden big-endian del valor (y al revés): intercambia los bytes de cada registro (BADC, DCBA) y
    /// el orden de los registros (CDAB, DCBA). En los textos solo se aplica el intercambio de bytes: los caracteres siguen el
    /// orden de los registros.
    /// </summary>
    private static void Normalize(ModbusAddress address, Span<byte> d, ModbusByteOrder order)
    {
        if (order is ModbusByteOrder.BADC or ModbusByteOrder.DCBA)
        {
            for (var i = 0; i + 1 < d.Length; i += 2)
                (d[i], d[i + 1]) = (d[i + 1], d[i]);
        }
        if ((order is ModbusByteOrder.CDAB or ModbusByteOrder.DCBA) && address.ValueType != ModbusValueType.String && d.Length > 2)
        {
            var registers = d.Length / 2;
            Span<byte> tmp = stackalloc byte[2];
            for (var i = 0; i < registers / 2; i++)
            {
                var a = d.Slice(i * 2, 2);
                var b = d.Slice((registers - 1 - i) * 2, 2);
                a.CopyTo(tmp);
                b.CopyTo(a);
                tmp.CopyTo(b);
            }
        }
    }

    private static string DecodeString(ModbusAddress address, ReadOnlySpan<byte> d)
    {
        var chars = d[..address.StringLength];
        var end = chars.IndexOf((byte)0);
        return Text.GetString(end >= 0 ? chars[..end] : chars).TrimEnd(' ');
    }

    private static void EncodeString(ModbusAddress address, string s, Span<byte> d)
    {
        var bytes = Text.GetBytes(s);
        if (bytes.Length > address.StringLength)
            throw new ArgumentException($"El texto tiene {bytes.Length} caracteres y el STRING admite {address.StringLength}.");
        d.Clear();
        bytes.CopyTo(d);
    }
}
