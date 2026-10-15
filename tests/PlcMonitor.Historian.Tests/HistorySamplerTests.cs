using PlcMonitor.Abstractions;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Historian.Tests;

public class HistorySamplerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static TagValue Good(object value, double atSeconds) => TagValue.Good(value, TagDataType.Double, T0.AddSeconds(atSeconds));

    private static HistorySampler Sampler(int minMs = 0, double deadband = 0, DeadbandMode mode = DeadbandMode.Absolute, int maxMs = 0) =>
        new(new TagKey("plc", "t"), new TagHistorySettings { Enabled = true, MinIntervalMs = minMs, Deadband = deadband, DeadbandMode = mode, MaxIntervalMs = maxMs });

    [Fact]
    public void First_value_and_quality_changes_are_always_stored()
    {
        var s = Sampler(minMs: 10_000, deadband: 100);
        s.Offer(Good(1.0, 0), T0).ShouldNotBeNull();
        var bad = TagValue.Bad(QualityCode.BadNotConnected, T0.AddSeconds(1), "sin conexión");
        s.Offer(bad, T0.AddSeconds(1))!.Quality.ShouldBe(QualityCode.BadNotConnected);
        s.Offer(Good(1.0, 2), T0.AddSeconds(2))!.Quality.ShouldBe(QualityCode.Good);
    }

    [Theory]
    [InlineData(DeadbandMode.Absolute, 0.5, 10.4, false)]
    [InlineData(DeadbandMode.Absolute, 0.5, 10.6, true)]
    [InlineData(DeadbandMode.Absolute, 0.5, 9.4, true)]
    [InlineData(DeadbandMode.Percent, 5, 10.4, false)]
    [InlineData(DeadbandMode.Percent, 5, 10.6, true)]
    public void Deadband_filters_small_numeric_changes(DeadbandMode mode, double deadband, double next, bool stored)
    {
        var s = Sampler(deadband: deadband, mode: mode);
        s.Offer(Good(10.0, 0), T0);
        (s.Offer(Good(next, 1), T0.AddSeconds(1)) is not null).ShouldBe(stored);
    }

    [Fact]
    public void Deadband_does_not_apply_to_text()
    {
        var s = Sampler(deadband: 100);
        s.Offer(TagValue.Good("A", TagDataType.String, T0), T0);
        s.Offer(TagValue.Good("B", TagDataType.String, T0.AddSeconds(1)), T0.AddSeconds(1)).ShouldNotBeNull();
    }

    [Fact]
    public void Faster_changes_keep_the_last_one_until_the_minimum_interval()
    {
        var s = Sampler(minMs: 1000);
        s.Offer(Good(1.0, 0), T0).ShouldNotBeNull();
        s.Offer(Good(2.0, 0.1), T0.AddMilliseconds(100)).ShouldBeNull();
        s.Offer(Good(3.0, 0.2), T0.AddMilliseconds(200)).ShouldBeNull();
        s.Tick(T0.AddMilliseconds(999)).ShouldBeNull();

        var flushed = s.Tick(T0.AddMilliseconds(1000)).ShouldNotBeNull();

        flushed.Value.ShouldBe(3.0);
        flushed.Timestamp.ShouldBe(T0.AddMilliseconds(200));
        s.Tick(T0.AddMilliseconds(1500)).ShouldBeNull();
        s.Offer(Good(4.0, 2.5), T0.AddSeconds(2.5)).ShouldNotBeNull();
    }

    [Fact]
    public void Maximum_interval_repeats_the_current_value()
    {
        var s = Sampler(maxMs: 5000, deadband: 10);
        s.Offer(Good(1.0, 0), T0);
        s.Offer(Good(2.0, 1), T0.AddSeconds(1)).ShouldBeNull(); // dentro de la banda muerta
        s.Tick(T0.AddSeconds(4.9)).ShouldBeNull();

        var heartbeat = s.Tick(T0.AddSeconds(5)).ShouldNotBeNull();

        heartbeat.Value.ShouldBe(2.0);
        heartbeat.Timestamp.ShouldBe(T0.AddSeconds(5));
        s.Tick(T0.AddSeconds(9.9)).ShouldBeNull();
        s.Tick(T0.AddSeconds(10)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(true, 1.0)]
    [InlineData(false, 0.0)]
    [InlineData((short)7, 7.0)]
    [InlineData(2.5f, 2.5)]
    public void Numbers_include_booleans(object value, double expected) => HistorySampler.ToNumber(value).ShouldBe(expected);

    [Fact]
    public void Text_is_not_a_number() => HistorySampler.ToNumber("x").ShouldBeNull();

    [Fact]
    public void Floats_keep_their_shortest_decimal_form() => HistorySampler.ToNumber(516.32196f).ShouldBe(516.32196);
}
