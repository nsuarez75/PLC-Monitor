using PlcMonitor.Abstractions;

namespace PlcMonitor.Core.Tests;

public class ValueParserTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("ON", true)]
    [InlineData("sí", true)]
    [InlineData("0", false)]
    [InlineData("False", false)]
    [InlineData("off", false)]
    public void Parses_booleans(string text, bool expected)
    {
        ValueParser.TryParse(text, TagDataType.Boolean, out var value, out _).ShouldBeTrue();
        value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("255", TagDataType.Byte, (byte)255)]
    [InlineData("0xFF", TagDataType.Byte, (byte)255)]
    [InlineData("16#FF", TagDataType.Byte, (byte)255)]
    [InlineData("-32768", TagDataType.Int16, (short)-32768)]
    [InlineData("65535", TagDataType.UInt16, (ushort)65535)]
    [InlineData("16#FFFF_FFFF", TagDataType.UInt32, uint.MaxValue)]
    [InlineData("-5", TagDataType.SByte, (sbyte)-5)]
    public void Parses_integers(string text, TagDataType type, object expected)
    {
        ValueParser.TryParse(text, type, out var value, out var error).ShouldBeTrue(error);
        value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("256", TagDataType.Byte)]
    [InlineData("-1", TagDataType.UInt16)]
    [InlineData("32768", TagDataType.Int16)]
    [InlineData("abc", TagDataType.Int32)]
    [InlineData("1,5", TagDataType.Float)]
    [InlineData("NaN", TagDataType.Double)]
    [InlineData("quizá", TagDataType.Boolean)]
    [InlineData("0xZZ", TagDataType.UInt32)]
    [InlineData("0G", TagDataType.ByteArray)]
    public void Rejects_out_of_range_or_malformed(string text, TagDataType type)
    {
        ValueParser.TryParse(text, type, out var value, out var error).ShouldBeFalse();
        value.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Floats_use_invariant_culture()
    {
        ValueParser.TryParse("3.25", TagDataType.Float, out var f, out _).ShouldBeTrue();
        f.ShouldBe(3.25f);
        ValueParser.TryParse("-1e3", TagDataType.Double, out var d, out _).ShouldBeTrue();
        d.ShouldBe(-1000d);
    }

    [Fact]
    public void Strings_are_kept_verbatim()
    {
        ValueParser.TryParse("  hola  ", TagDataType.String, out var s, out _).ShouldBeTrue();
        s.ShouldBe("  hola  ");
    }

    [Fact]
    public void Parses_utc_datetime()
    {
        ValueParser.TryParse("2026-10-14T12:30:00Z", TagDataType.DateTime, out var v, out _).ShouldBeTrue();
        v.ShouldBe(new DateTime(2026, 10, 14, 12, 30, 0, DateTimeKind.Utc));
        ((DateTime)v!).Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("1500", 1500)]
    [InlineData("1500ms", 1500)]
    [InlineData("00:00:02.5", 2500)]
    public void Parses_timespans(string text, int expectedMs)
    {
        ValueParser.TryParse(text, TagDataType.TimeSpan, out var v, out _).ShouldBeTrue();
        v.ShouldBe(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public void Parses_hex_byte_arrays()
    {
        ValueParser.TryParse("01 0A-ff", TagDataType.ByteArray, out var v, out _).ShouldBeTrue();
        v.ShouldBe(new byte[] { 0x01, 0x0A, 0xFF });
    }

    [Fact]
    public void Null_or_unknown_type_is_rejected()
    {
        ValueParser.TryParse(null, TagDataType.Int32, out _, out _).ShouldBeFalse();
        ValueParser.TryParse("1", TagDataType.Unknown, out _, out var error).ShouldBeFalse();
        error!.ShouldContain("Unknown");
    }
}
