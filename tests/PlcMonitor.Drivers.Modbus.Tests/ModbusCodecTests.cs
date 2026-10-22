namespace PlcMonitor.Drivers.Modbus.Tests;

public class ModbusCodecTests
{
    private static ModbusAddress A(string text) => ModbusAddress.Parse(text);

    [Theory]
    [InlineData(ModbusByteOrder.ABCD, "41480000")]
    [InlineData(ModbusByteOrder.CDAB, "00004148")]
    [InlineData(ModbusByteOrder.BADC, "48410000")]
    [InlineData(ModbusByteOrder.DCBA, "00004841")]
    public void Float_in_each_byte_order(ModbusByteOrder order, string hex)
    {
        // 12.5f = 0x41480000
        var registers = Convert.FromHexString(hex);
        ModbusCodec.Decode(A("HR0:FLOAT"), registers, order).ShouldBe(12.5f);
        ModbusCodec.Encode(A("HR0:FLOAT"), 12.5, order).ShouldBe(registers);
    }

    [Fact]
    public void Tag_order_overrides_the_device_order()
    {
        ModbusCodec.Decode(A("HR0:FLOAT:CDAB"), Convert.FromHexString("00004148"), ModbusByteOrder.ABCD).ShouldBe(12.5f);
    }

    [Theory]
    [InlineData(ModbusByteOrder.ABCD, "0102030405060708")]
    [InlineData(ModbusByteOrder.CDAB, "0708050603040102")]
    [InlineData(ModbusByteOrder.BADC, "0201040306050807")]
    [InlineData(ModbusByteOrder.DCBA, "0807060504030201")]
    public void Sixty_four_bits_in_each_byte_order(ModbusByteOrder order, string hex)
    {
        const long value = 0x0102030405060708;
        ModbusCodec.Decode(A("HR0:INT64"), Convert.FromHexString(hex), order).ShouldBe(value);
        ModbusCodec.Encode(A("HR0:INT64"), value, order).ShouldBe(Convert.FromHexString(hex));
    }

    [Fact]
    public void Sixteen_bits_only_swap_bytes()
    {
        var data = Convert.FromHexString("FFFE");
        ModbusCodec.Decode(A("HR0"), data, ModbusByteOrder.ABCD).ShouldBe((short)-2);
        ModbusCodec.Decode(A("HR0"), data, ModbusByteOrder.CDAB).ShouldBe((short)-2);
        ModbusCodec.Decode(A("HR0:UINT16"), data, ModbusByteOrder.BADC).ShouldBe((ushort)0xFEFF);
        ModbusCodec.Decode(A("HR0:UINT16"), data, ModbusByteOrder.DCBA).ShouldBe((ushort)0xFEFF);
    }

    [Fact]
    public void Decodes_all_numeric_types()
    {
        ModbusCodec.Decode(A("HR0:UINT32"), Convert.FromHexString("FFFFFFFF"), ModbusByteOrder.ABCD).ShouldBe(uint.MaxValue);
        ModbusCodec.Decode(A("HR0:INT32"), Convert.FromHexString("FFFFFFFF"), ModbusByteOrder.ABCD).ShouldBe(-1);
        ModbusCodec.Decode(A("HR0:UINT64"), Convert.FromHexString("FFFFFFFFFFFFFFFF"), ModbusByteOrder.ABCD).ShouldBe(ulong.MaxValue);
        ModbusCodec.Decode(A("HR0:DOUBLE"), Convert.FromHexString("4029000000000000"), ModbusByteOrder.ABCD).ShouldBe(12.5);
    }

    [Fact]
    public void Register_bits_ignore_the_byte_order()
    {
        var data = Convert.FromHexString("0008"); // bit 3
        ModbusCodec.Decode(A("HR0.3"), data, ModbusByteOrder.DCBA).ShouldBe(true);
        ModbusCodec.Decode(A("HR0.11"), data, ModbusByteOrder.DCBA).ShouldBe(false);
        ModbusCodec.SetRegisterBit(data, 15, true).ShouldBe(Convert.FromHexString("8008"));
        ModbusCodec.SetRegisterBit(data, 3, false).ShouldBe(Convert.FromHexString("0000"));
    }

    [Fact]
    public void Strings_keep_register_order_and_stop_at_nul()
    {
        var hola = "HOLA\0\0"u8.ToArray();
        ModbusCodec.Decode(A("HR0:STRING[6]"), hola, ModbusByteOrder.ABCD).ShouldBe("HOLA");
        // Intercambio de bytes: cada registro lleva sus dos caracteres al revés.
        ModbusCodec.Decode(A("HR0:STRING[4]"), "OHAL"u8.ToArray(), ModbusByteOrder.BADC).ShouldBe("HOLA");
        // El intercambio de palabras no cambia el orden de los caracteres.
        ModbusCodec.Decode(A("HR0:STRING[4]"), "HOLA"u8.ToArray(), ModbusByteOrder.CDAB).ShouldBe("HOLA");
        ModbusCodec.Encode(A("HR0:STRING[5]"), "AB", ModbusByteOrder.ABCD).ShouldBe("AB\0\0\0\0"u8.ToArray());
        ModbusCodec.Encode(A("HR0:STRING[4]"), "HOLA", ModbusByteOrder.BADC).ShouldBe("OHAL"u8.ToArray());
    }

    [Fact]
    public void Encode_rejects_values_that_do_not_fit()
    {
        Should.Throw<OverflowException>(() => ModbusCodec.Encode(A("HR0"), 40000, ModbusByteOrder.ABCD));
        Should.Throw<OverflowException>(() => ModbusCodec.Encode(A("HR0:UINT16"), -1, ModbusByteOrder.ABCD));
        Should.Throw<FormatException>(() => ModbusCodec.Encode(A("HR0:FLOAT"), "abc", ModbusByteOrder.ABCD));
        Should.Throw<ArgumentException>(() => ModbusCodec.Encode(A("HR0:STRING[2]"), "ABC", ModbusByteOrder.ABCD));
        Should.Throw<InvalidOperationException>(() => ModbusCodec.Encode(A("C0"), true, ModbusByteOrder.ABCD));
    }

    [Fact]
    public void Reads_packed_bits()
    {
        var packed = new byte[] { 0b0000_0101, 0b1000_0000 };
        ModbusCodec.GetBit(packed, 0).ShouldBeTrue();
        ModbusCodec.GetBit(packed, 1).ShouldBeFalse();
        ModbusCodec.GetBit(packed, 2).ShouldBeTrue();
        ModbusCodec.GetBit(packed, 15).ShouldBeTrue();
    }
}
