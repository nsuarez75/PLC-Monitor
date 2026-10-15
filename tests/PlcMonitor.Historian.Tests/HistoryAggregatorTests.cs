using PlcMonitor.Abstractions;

namespace PlcMonitor.Historian.Tests;

public class HistoryAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static HistoryPoint P(double seconds, double? value, QualityCode q = QualityCode.Good) => new(T0.AddSeconds(seconds), value, null, q);

    private static readonly HistoryPoint[] Points =
    [
        P(0, 1), P(10, 3), P(59, 2),            // minuto 0
        P(61, 10), P(70, null), P(80, 99, QualityCode.Bad), // minuto 1 (texto y mala calidad se ignoran)
        P(185, 7),                              // minuto 3 (el 2 está vacío)
    ];

    [Theory]
    [InlineData(HistoryAggregate.Average, new[] { 2.0, 10, 7 })]
    [InlineData(HistoryAggregate.Min, new[] { 1.0, 10, 7 })]
    [InlineData(HistoryAggregate.Max, new[] { 3.0, 10, 7 })]
    [InlineData(HistoryAggregate.First, new[] { 1.0, 10, 7 })]
    [InlineData(HistoryAggregate.Last, new[] { 2.0, 10, 7 })]
    public void Values_are_grouped_by_interval(HistoryAggregate aggregate, double[] expected)
    {
        var result = HistoryAggregator.Aggregate(Points, T0, TimeSpan.FromMinutes(1), aggregate);
        result.Select(p => p.Number!.Value).ShouldBe(expected);
        result.Select(p => p.Timestamp).ShouldBe([T0, T0.AddMinutes(1), T0.AddMinutes(3)]);
    }
}
