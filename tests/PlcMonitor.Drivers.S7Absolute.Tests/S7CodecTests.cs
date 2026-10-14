namespace PlcMonitor.Drivers.S7Absolute.Tests;

public class S7CodecTests
{
    [Theory]
    [InlineData("DB1.DBX0.0", new byte[] { 0b0000_0001 }, true)]
    [InlineData("DB1.DBX0.3", new byte[] { 0b0000_1000 }, true)]
    [InlineData("DB1.DBX0.3", new byte[] { 0b1111_0111 }, false)]
    [InlineData("DB1.DBB0", new byte[] { 0xAB }, (byte)0xAB)]
    [InlineData("DB1.DBW0", new byte[] { 0x12, 0x34 }, (ushort)0x1234)]
    [InlineData("DB1.INT0", new byte[] { 0xFF, 0xFE }, (short)-2)]
    [InlineData("DB1.DBD0", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, 0xDEADBEEFu)]
    [InlineData("DB1.DINT0", new byte[] { 0xFF, 0xFF, 0xFF, 0x85 }, -123)]
    [InlineData("DB1.REAL0", new byte[] { 0x40, 0x49, 0x0F, 0xDB }, 3.1415927f)]
    [InlineData("DB1.LREAL0", new byte[] { 0x40, 0x09, 0x21, 0xFB, 0x54, 0x44, 0x2D, 0x18 }, Math.PI)]
    [InlineData("DB1.LINT0", new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 0 }, long.MinValue)]
    [InlineData("DB1.L0", new byte[] { 0, 0, 0, 0, 0, 0, 0x01, 0x00 }, 256UL)]
    [InlineData("DB1.C0", new byte[] { (byte)'A' }, "A")]
    public void Decodes_big_endian_values(string address, byte[] data, object expected) =>
        S7Codec.Decode(S7Address.Parse(address), data).ShouldBe(expected);

    [Theory]
    [InlineData("DB1.DBB0", (byte)7)]
    [InlineData("DB1.DBW0", (ushort)65535)]
    [InlineData("DB1.INT0", (short)-32768)]
    [InlineData("DB1.DBD0", 4000000000u)]
    [InlineData("DB1.DINT0", int.MinValue)]
    [InlineData("DB1.REAL0", -12.5f)]
    [InlineData("DB1.LREAL0", 1e300)]
    [InlineData("DB1.LINT0", long.MaxValue)]
    [InlineData("DB1.L0", ulong.MaxValue)]
    [InlineData("DB1.C0", "z")]
    public void Encode_then_decode_round_trips(string address, object value)
    {
        var a = S7Address.Parse(address);
        S7Codec.Decode(a, S7Codec.Encode(a, value)).ShouldBe(value);
    }

    [Fact]
    public void Encode_converts_compatible_numeric_types()
    {
        var a = S7Address.Parse("DB1.INT0");
        S7Codec.Encode(a, 100).ShouldBe(new byte[] { 0x00, 0x64 });
        S7Codec.Encode(a, "100").ShouldBe(new byte[] { 0x00, 0x64 });
    }

    [Fact]
    public void Encode_rejects_overflow() =>
        Should.Throw<OverflowException>(() => S7Codec.Encode(S7Address.Parse("DB1.INT0"), 40000));

    [Fact]
    public void Strings_use_s7_header_and_current_length()
    {
        var a = S7Address.Parse("DB1.STRING0[10]");
        var encoded = S7Codec.Encode(a, "Hola");
        encoded.Length.ShouldBe(12);
        encoded[0].ShouldBe((byte)10);
        encoded[1].ShouldBe((byte)4);
        S7Codec.Decode(a, encoded).ShouldBe("Hola");
    }

    [Fact]
    public void String_decode_never_exceeds_declared_length()
    {
        var a = S7Address.Parse("DB1.STRING0[4]");
        // Cabecera corrupta: dice 200 de longitud actual.
        S7Codec.Decode(a, new byte[] { 4, 200, (byte)'a', (byte)'b', (byte)'c', (byte)'d' }).ShouldBe("abcd");
    }

    [Fact]
    public void String_too_long_is_rejected() =>
        Should.Throw<ArgumentException>(() => S7Codec.Encode(S7Address.Parse("DB1.STRING0[3]"), "demasiado"));

    [Fact]
    public void Strings_support_latin1_characters()
    {
        var a = S7Address.Parse("DB1.STRING0[10]");
        S7Codec.Decode(a, S7Codec.Encode(a, "Año ñ º")).ShouldBe("Año ñ º");
    }

    [Fact]
    public void DateAndTime_is_bcd_encoded()
    {
        var a = S7Address.Parse("DB1.DT0");
        var t = new DateTime(2026, 10, 14, 15, 30, 45, 123);
        var encoded = S7Codec.Encode(a, t);
        encoded.ShouldBe(new byte[] { 0x26, 0x10, 0x14, 0x15, 0x30, 0x45, 0x12, 0x34 }); // 14/10/2026 = miércoles -> 4
        S7Codec.Decode(a, encoded).ShouldBe(t);
    }

    [Fact]
    public void DateAndTime_years_90_to_99_are_1900s()
    {
        var a = S7Address.Parse("DB1.DT0");
        ((DateTime)S7Codec.Decode(a, new byte[] { 0x95, 0x01, 0x02, 0, 0, 0, 0, 0x01 })).Year.ShouldBe(1995);
    }

    [Fact]
    public void DateAndTime_out_of_range_is_rejected() =>
        Should.Throw<ArgumentOutOfRangeException>(() => S7Codec.Encode(S7Address.Parse("DB1.DT0"), new DateTime(2100, 1, 1)));

    [Fact]
    public void Arrays_decode_to_typed_arrays()
    {
        var a = S7Address.Parse("DB1.INT0[3]");
        var value = S7Codec.Decode(a, new byte[] { 0, 1, 0, 2, 0xFF, 0xFF });
        value.ShouldBe(new short[] { 1, 2, -1 });
        S7Codec.Encode(a, value).ShouldBe(new byte[] { 0, 1, 0, 2, 0xFF, 0xFF });
    }

    [Fact]
    public void Byte_arrays_are_raw()
    {
        var a = S7Address.Parse("DB1.B0[4]");
        S7Codec.Decode(a, new byte[] { 1, 2, 3, 4, 5 }).ShouldBe(new byte[] { 1, 2, 3, 4 });
        S7Codec.Encode(a, new byte[] { 9, 8, 7, 6 }).ShouldBe(new byte[] { 9, 8, 7, 6 });
    }

    [Fact]
    public void Array_length_mismatch_is_rejected() =>
        Should.Throw<ArgumentException>(() => S7Codec.Encode(S7Address.Parse("DB1.INT0[3]"), new short[] { 1, 2 }));

    [Fact]
    public void Short_buffer_is_rejected() =>
        Should.Throw<ArgumentException>(() => S7Codec.Decode(S7Address.Parse("DB1.DINT0"), new byte[] { 1, 2 }));

    [Fact]
    public void Bits_cannot_be_encoded_as_bytes() =>
        Should.Throw<InvalidOperationException>(() => S7Codec.Encode(S7Address.Parse("DB1.DBX0.0"), true));
}
