using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.Tests;

/// <summary>Tendencia de disparo: tramos en que se cumple (con tiempo antes y después) y puntos que se ven.</summary>
public class TriggerFilterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    private static ChartPoint P(double seconds, double? value) => new(T0.AddSeconds(seconds), value);

    private static bool On(double? v) => TriggerFilter.IsMet(v, TriggerCondition.Equal, 1);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Spans_cover_the_condition_plus_the_time_after()
    {
        var trigger = new[] { P(0, 0), P(10, 1), P(20, 0), P(60, 1) };
        TriggerFilter.Spans(trigger, On, TimeSpan.FromSeconds(5)).ShouldBe([new(At(10), At(25)), new(At(60), null)]);
    }

    [Fact]
    public void Spans_start_earlier_with_the_time_before()
    {
        var trigger = new[] { P(0, 0), P(10, 1), P(20, 0) };
        TriggerFilter.Spans(trigger, On, TimeSpan.Zero, TimeSpan.FromSeconds(8)).ShouldBe([new(At(2), At(20))]);
    }

    [Fact]
    public void Overlapping_spans_are_joined()
    {
        // Cae a los 20 s (+5 s) y vuelve a los 30 s, que con 6 s de antes empieza a los 24 s: un solo tramo.
        var trigger = new[] { P(10, 1), P(20, 0), P(30, 1), P(40, 0) };
        TriggerFilter.Spans(trigger, On, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6)).ShouldBe([new(At(4), At(45))]);
        TriggerFilter.Spans(trigger, On, TimeSpan.FromSeconds(5)).Count.ShouldBe(2);
    }

    [Fact]
    public void Bad_quality_is_not_met() =>
        TriggerFilter.Spans([P(0, 1), P(5, null)], On, TimeSpan.Zero).ShouldBe([new(At(0), At(5))]);

    [Theory]
    [InlineData(TriggerCondition.Greater, "10", 10.5, true)]
    [InlineData(TriggerCondition.LessOrEqual, "2,5", 2.5, true)]
    [InlineData(TriggerCondition.NotEqual, "false", 1, true)]
    [InlineData(TriggerCondition.Equal, "true", 0, false)]
    public void Conditions_compare_numbers_and_booleans(TriggerCondition condition, string target, double value, bool met)
    {
        TriggerFilter.TryParseTarget(target, out var t).ShouldBeTrue();
        TriggerFilter.IsMet(value, condition, t).ShouldBe(met);
    }

    [Fact]
    public void Only_points_inside_the_spans_are_shown_with_a_gap_after_each()
    {
        var points = new[] { P(0, 1), P(5, 2), P(12, 3), P(18, 4), P(30, 5), P(65, 6) };
        var output = new List<ChartPoint>();
        TriggerFilter.Apply(points, [new(At(10), At(25)), new(At(60), null)], At(70), output);

        // Tramo 1: el valor que tenía al empezar (2), los de dentro y un hueco al acabar; tramo 2 abierto hasta ahora.
        output.ShouldBe([P(10, 2), P(12, 3), P(18, 4), P(25, null), P(60, 5), P(65, 6)]);
    }
}
