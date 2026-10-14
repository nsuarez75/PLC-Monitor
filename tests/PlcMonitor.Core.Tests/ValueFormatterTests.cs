using PlcMonitor.Abstractions;

namespace PlcMonitor.Core.Tests;

public class ValueFormatterTests
{
    [Theory]
    [InlineData(null, "null")]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData("texto", "texto")]
    [InlineData(1.5f, "1.5")]
    [InlineData(-0.1, "-0.1")]
    [InlineData((short)-7, "-7")]
    [InlineData(4000000000u, "4000000000")]
    public void Formats_scalars_invariantly(object? value, string expected) =>
        ValueFormatter.Format(value).ShouldBe(expected);

    [Fact]
    public void Formats_dates_and_times()
    {
        ValueFormatter.Format(new DateTime(2026, 10, 14, 8, 5, 3, 7, DateTimeKind.Utc)).ShouldBe("2026-10-14 08:05:03.007Z");
        ValueFormatter.Format(new DateTime(2026, 10, 14, 8, 5, 3, 7)).ShouldBe("2026-10-14 08:05:03.007");
        ValueFormatter.Format(TimeSpan.FromMilliseconds(1500)).ShouldBe("00:00:01.5000000");
    }

    [Fact]
    public void Formats_bytes_as_hex() =>
        ValueFormatter.Format(new byte[] { 0x01, 0xAB }).ShouldBe("01AB");

    [Fact]
    public void Formats_arrays_and_truncates_long_ones()
    {
        ValueFormatter.Format(new short[] { 1, 2, 3 }).ShouldBe("[1, 2, 3]");
        var big = ValueFormatter.Format(Enumerable.Range(0, 40).ToArray());
        big.ShouldEndWith(", … (40 elementos)]");
        big.Split(',').Length.ShouldBe(ValueFormatter.MaxArrayItems + 1);
    }

    [Fact]
    public void Bad_values_show_quality_and_detail()
    {
        ValueFormatter.Format(TagValue.Bad(QualityCode.BadNotConnected, DateTimeOffset.UnixEpoch, "sin red"))
            .ShouldBe("<BadNotConnected: sin red>");
        ValueFormatter.Format(TagValue.Good(3, TagDataType.Int32, DateTimeOffset.UnixEpoch)).ShouldBe("3");
    }
}
