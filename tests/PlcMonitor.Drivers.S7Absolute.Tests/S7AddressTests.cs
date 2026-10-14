using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.S7Absolute.Tests;

public class S7AddressTests
{
    [Theory]
    [InlineData("DB1.DBX0.3", S7Area.DataBlock, 1, 0, 3, S7ValueType.Bit)]
    [InlineData("db1.dbx0.3", S7Area.DataBlock, 1, 0, 3, S7ValueType.Bit)]
    [InlineData("DB1.X0.3", S7Area.DataBlock, 1, 0, 3, S7ValueType.Bit)]
    [InlineData("DB10.DBB4", S7Area.DataBlock, 10, 4, 0, S7ValueType.Byte)]
    [InlineData("DB1.DBW10", S7Area.DataBlock, 1, 10, 0, S7ValueType.Word)]
    [InlineData("DB1.DBD20", S7Area.DataBlock, 1, 20, 0, S7ValueType.DWord)]
    [InlineData("DB1.INT6", S7Area.DataBlock, 1, 6, 0, S7ValueType.Int)]
    [InlineData("DB1.I6", S7Area.DataBlock, 1, 6, 0, S7ValueType.Int)]
    [InlineData("DB1.DI8", S7Area.DataBlock, 1, 8, 0, S7ValueType.DInt)]
    [InlineData("DB1.DINT8", S7Area.DataBlock, 1, 8, 0, S7ValueType.DInt)]
    [InlineData("DB1.REAL20", S7Area.DataBlock, 1, 20, 0, S7ValueType.Real)]
    [InlineData("DB1.DBREAL20", S7Area.DataBlock, 1, 20, 0, S7ValueType.Real)]
    [InlineData("DB1.LREAL0", S7Area.DataBlock, 1, 0, 0, S7ValueType.LReal)]
    [InlineData("DB1.LI0", S7Area.DataBlock, 1, 0, 0, S7ValueType.LInt)]
    [InlineData("DB1.L0", S7Area.DataBlock, 1, 0, 0, S7ValueType.LWord)]
    [InlineData("DB1.DT0", S7Area.DataBlock, 1, 0, 0, S7ValueType.DateAndTime)]
    [InlineData("DB1.C3", S7Area.DataBlock, 1, 3, 0, S7ValueType.Char)]
    [InlineData("DB65535.DBB0", S7Area.DataBlock, 65535, 0, 0, S7ValueType.Byte)]
    [InlineData("M10.1", S7Area.Merker, 0, 10, 1, S7ValueType.Bit)]
    [InlineData("MX10.1", S7Area.Merker, 0, 10, 1, S7ValueType.Bit)]
    [InlineData("MW20", S7Area.Merker, 0, 20, 0, S7ValueType.Word)]
    [InlineData("MREAL100", S7Area.Merker, 0, 100, 0, S7ValueType.Real)]
    [InlineData("I0.0", S7Area.Inputs, 0, 0, 0, S7ValueType.Bit)]
    [InlineData("E0.7", S7Area.Inputs, 0, 0, 7, S7ValueType.Bit)]
    [InlineData("IW64", S7Area.Inputs, 0, 64, 0, S7ValueType.Word)]
    [InlineData("IB3", S7Area.Inputs, 0, 3, 0, S7ValueType.Byte)]
    [InlineData("Q4.0", S7Area.Outputs, 0, 4, 0, S7ValueType.Bit)]
    [InlineData("AW10", S7Area.Outputs, 0, 10, 0, S7ValueType.Word)]
    [InlineData(" DB1.DBW 10 ", S7Area.DataBlock, 1, 10, 0, S7ValueType.Word)]
    public void Parses_valid_addresses(string text, S7Area area, int db, int offset, int bit, S7ValueType type)
    {
        S7Address.TryParse(text, out var a, out var error).ShouldBeTrue(error);
        a.Area.ShouldBe(area);
        a.DbNumber.ShouldBe(db);
        a.ByteOffset.ShouldBe(offset);
        a.BitIndex.ShouldBe(bit);
        a.ValueType.ShouldBe(type);
        a.ArrayCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DB0.DBW0")]
    [InlineData("DB1.DBX0")]
    [InlineData("DB1.DBW0.1")]
    [InlineData("DB1.DBX0.8")]
    [InlineData("M10")]
    [InlineData("X10.1")]
    [InlineData("DB1.FOO0")]
    [InlineData("DB1.STRING0[0]")]
    [InlineData("DB1.STRING0[255]")]
    [InlineData("DB1.INT0[0]")]
    [InlineData("DB1.INT0[5000]")]
    [InlineData("DB1.DBX0.1[4]")]
    [InlineData("DB1.DT0[2]")]
    [InlineData("DB123456.DBW0")]
    public void Rejects_invalid_addresses(string text)
    {
        S7Address.TryParse(text, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<FormatException>(() => S7Address.Parse(text));
    }

    [Fact]
    public void String_uses_bracket_as_length_and_default_254()
    {
        var s = S7Address.Parse("DB5.STRING30[20]");
        s.ValueType.ShouldBe(S7ValueType.String);
        s.StringLength.ShouldBe(20);
        s.ByteLength.ShouldBe(22);
        s.DataType.ShouldBe(TagDataType.String);

        S7Address.Parse("DB5.STRING30").StringLength.ShouldBe(254);
    }

    [Fact]
    public void Brackets_on_numeric_types_define_arrays()
    {
        var a = S7Address.Parse("DB1.REAL0[10]");
        a.ArrayCount.ShouldBe(10);
        a.IsArray.ShouldBeTrue();
        a.ByteLength.ShouldBe(40);
        a.DataType.ShouldBe(TagDataType.Float);

        var bytes = S7Address.Parse("DB1.B0[16]");
        bytes.DataType.ShouldBe(TagDataType.ByteArray);
        bytes.ByteLength.ShouldBe(16);
    }

    [Theory]
    [InlineData("DB1.DBX0.3", TagDataType.Boolean, 1)]
    [InlineData("DB1.DBW0", TagDataType.UInt16, 2)]
    [InlineData("DB1.INT0", TagDataType.Int16, 2)]
    [InlineData("DB1.DBD0", TagDataType.UInt32, 4)]
    [InlineData("DB1.DINT0", TagDataType.Int32, 4)]
    [InlineData("DB1.REAL0", TagDataType.Float, 4)]
    [InlineData("DB1.L0", TagDataType.UInt64, 8)]
    [InlineData("DB1.LINT0", TagDataType.Int64, 8)]
    [InlineData("DB1.LREAL0", TagDataType.Double, 8)]
    [InlineData("DB1.DT0", TagDataType.DateTime, 8)]
    [InlineData("DB1.C0", TagDataType.String, 1)]
    public void Maps_types_and_sizes(string text, TagDataType type, int size)
    {
        var a = S7Address.Parse(text);
        a.DataType.ShouldBe(type);
        a.ByteLength.ShouldBe(size);
    }

    [Theory]
    [InlineData("db1.dbx0.3", "DB1.X0.3")]
    [InlineData("DB1.DBW10", "DB1.W10")]
    [InlineData("DB1.I6", "DB1.INT6")]
    [InlineData("DB5.STRING30", "DB5.STRING30[254]")]
    [InlineData("DB1.REAL0[10]", "DB1.REAL0[10]")]
    [InlineData("E0.7", "IX0.7")]
    [InlineData("AW10", "QW10")]
    [InlineData("MW20", "MW20")]
    public void Canonical_form_round_trips(string text, string canonical)
    {
        var a = S7Address.Parse(text);
        a.ToString().ShouldBe(canonical);
        S7Address.Parse(a.ToString()).ShouldBe(a);
    }
}
