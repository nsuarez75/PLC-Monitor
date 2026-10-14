using PlcMonitor.Abstractions;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcMonitor.Drivers.S7Symbolic.Tests;

public class PlcTagValueMapperTests
{
    private static PlcTag Tag(uint softdatatype, bool array = false) =>
        PlcTags.TagFactory("T", new ItemAddress(), softdatatype, array);

    [Fact]
    public void Reads_value_processed_by_the_library()
    {
        var tag = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_INT);
        tag.ProcessReadResult(new ValueInt(-42), 0);

        PlcTagValueMapper.QualityOf(tag).ShouldBe(QualityCode.Good);
        PlcTagValueMapper.GetValue(tag).ShouldBe(((object?)(short)-42, TagDataType.Int16));
    }

    [Fact]
    public void Read_error_from_cpu_is_bad_address()
    {
        var tag = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_INT);
        tag.ProcessReadResult(null!, 0x8104);
        PlcTagValueMapper.QualityOf(tag).ShouldBe(QualityCode.BadAddressInvalid);
    }

    [Fact]
    public void Wrong_value_type_from_cpu_is_bad()
    {
        var tag = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_INT);
        tag.ProcessReadResult(new ValueReal(1f), 0);
        PlcTagValueMapper.QualityOf(tag).ShouldBe(QualityCode.Bad);
    }

    [Theory]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_BOOL, true, TagDataType.Boolean)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_BYTE, (byte)200, TagDataType.Byte)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_USINT, (byte)7, TagDataType.Byte)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_SINT, (sbyte)-7, TagDataType.SByte)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_WORD, (ushort)0xBEEF, TagDataType.UInt16)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_UINT, (ushort)65000, TagDataType.UInt16)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_INT, (short)-3, TagDataType.Int16)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_DWORD, 0xDEADBEEFu, TagDataType.UInt32)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_UDINT, 7u, TagDataType.UInt32)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_DINT, -70000, TagDataType.Int32)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_LINT, long.MinValue, TagDataType.Int64)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_ULINT, ulong.MaxValue, TagDataType.UInt64)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_LWORD, 1UL, TagDataType.UInt64)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_REAL, 1.5f, TagDataType.Float)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_LREAL, -2.25, TagDataType.Double)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_STRING, "hola", TagDataType.String)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_WSTRING, "ñandú", TagDataType.String)]
    public void Set_then_get_round_trips(uint softdatatype, object value, TagDataType expectedType)
    {
        var tag = Tag(softdatatype);
        PlcTagValueMapper.TrySetValue(tag, value, out var error).ShouldBeTrue(error);
        PlcTagValueMapper.GetValue(tag).ShouldBe((value, expectedType));
        PlcTagValueMapper.DataTypeOf(softdatatype).ShouldBe(expectedType);
    }

    [Fact]
    public void Set_converts_from_compatible_types()
    {
        var tag = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_INT);
        PlcTagValueMapper.TrySetValue(tag, 12, out _).ShouldBeTrue();
        PlcTagValueMapper.GetValue(tag).Value.ShouldBe((short)12);
        PlcTagValueMapper.TrySetValue(tag, "34", out _).ShouldBeTrue();
        PlcTagValueMapper.GetValue(tag).Value.ShouldBe((short)34);
    }

    [Theory]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_INT, 40000)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_REAL, "abc")]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_BYTE, -1)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_CHAR, "ab")]
    public void Set_rejects_incompatible_values(uint softdatatype, object value)
    {
        PlcTagValueMapper.TrySetValue(Tag(softdatatype), value, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Set_rejects_null() =>
        PlcTagValueMapper.TrySetValue(Tag(Softdatatype.S7COMMP_SOFTDATATYPE_INT), null, out _).ShouldBeFalse();

    [Fact]
    public void Chars_are_exposed_as_strings()
    {
        var tag = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_CHAR);
        PlcTagValueMapper.TrySetValue(tag, "Z", out _).ShouldBeTrue();
        PlcTagValueMapper.GetValue(tag).ShouldBe(((object?)"Z", TagDataType.String));
    }

    [Fact]
    public void Time_types_are_timespans()
    {
        var ltime = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_LTIME);
        PlcTagValueMapper.TrySetValue(ltime, TimeSpan.FromSeconds(1.5), out _).ShouldBeTrue();
        ((PlcTagLTime)ltime).Value.ShouldBe(1_500_000_000L); // nanosegundos
        PlcTagValueMapper.GetValue(ltime).ShouldBe(((object?)TimeSpan.FromSeconds(1.5), TagDataType.TimeSpan));

        var tod = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_TIMEOFDAY);
        PlcTagValueMapper.TrySetValue(tod, "12:30:00", out _).ShouldBeTrue();
        PlcTagValueMapper.GetValue(tod).Value.ShouldBe(new TimeSpan(12, 30, 0));

        var time = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_TIME);
        PlcTagValueMapper.TrySetValue(time, 2500, out _).ShouldBeTrue();
        PlcTagValueMapper.GetValue(time).ShouldBe(((object?)TimeSpan.FromMilliseconds(2500), TagDataType.TimeSpan));
    }

    [Fact]
    public void S5Time_uses_time_base()
    {
        var tag = (PlcTagS5Time)Tag(Softdatatype.S7COMMP_SOFTDATATYPE_S5TIME);
        tag.TimeValue = 25;
        tag.TimeBase = 2; // 1 s
        PlcTagValueMapper.GetValue(tag).Value.ShouldBe(TimeSpan.FromSeconds(25));
        PlcTagValueMapper.TrySetValue(tag, 1000, out var error).ShouldBeFalse();
        error!.ShouldContain("S5TIME");
    }

    [Fact]
    public void Ldt_is_utc_nanoseconds_since_1970()
    {
        var tag = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_LDT);
        var t = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        PlcTagValueMapper.TrySetValue(tag, t, out _).ShouldBeTrue();
        ((PlcTagLDT)tag).Value.ShouldBe((ulong)(t - DateTime.UnixEpoch).Ticks * 100);
        PlcTagValueMapper.GetValue(tag).Value.ShouldBe(t);
    }

    [Fact]
    public void Arrays_round_trip_with_element_conversion()
    {
        var tag = Tag(Softdatatype.S7COMMP_SOFTDATATYPE_INT, array: true);
        PlcTagValueMapper.TrySetValue(tag, new[] { 1, 2, 3 }, out var error).ShouldBeTrue(error);
        var (value, type) = PlcTagValueMapper.GetValue(tag);
        value.ShouldBe(new short[] { 1, 2, 3 });
        type.ShouldBe(TagDataType.Int16);
    }

    [Theory]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL, TagDataType.Boolean)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_BLOCKFB, TagDataType.UInt16)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_DTL, TagDataType.DateTime)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_TIME, TagDataType.TimeSpan)]
    [InlineData(Softdatatype.S7COMMP_SOFTDATATYPE_ANY, TagDataType.ByteArray)]
    public void Data_type_is_derived_from_library_tag_class(uint softdatatype, TagDataType expected) =>
        PlcTagValueMapper.DataTypeOf(softdatatype).ShouldBe(expected);

    [Fact]
    public void Native_type_names_come_from_library()
    {
        PlcTagValueMapper.NativeTypeName(Softdatatype.S7COMMP_SOFTDATATYPE_REAL).ShouldBe("Real");
        PlcTagValueMapper.NativeTypeName(0xFFFF).ShouldBe("0xFFFF");
        PlcTagValueMapper.DataTypeOf(0xFFFF).ShouldBe(TagDataType.Unknown);
    }
}
