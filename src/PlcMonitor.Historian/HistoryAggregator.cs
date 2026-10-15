namespace PlcMonitor.Historian;

/// <summary>Agrega puntos por intervalos fijos (desde <c>from</c>). Solo usa puntos numéricos con calidad buena; los intervalos vacíos no aparecen.</summary>
internal static class HistoryAggregator
{
    public static IReadOnlyList<HistoryPoint> Aggregate(IReadOnlyList<HistoryPoint> points, DateTimeOffset from, TimeSpan interval, HistoryAggregate aggregate)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "El intervalo debe ser positivo.");
        return points
            .Where(p => p.Number is not null && p.Quality == Abstractions.QualityCode.Good)
            .GroupBy(p => (p.Timestamp - from).Ticks / interval.Ticks)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var values = g.Select(p => p.Number!.Value).ToArray();
                var value = aggregate switch
                {
                    HistoryAggregate.Min => values.Min(),
                    HistoryAggregate.Max => values.Max(),
                    HistoryAggregate.First => values[0],
                    HistoryAggregate.Last => values[^1],
                    _ => values.Average(),
                };
                return new HistoryPoint(from + TimeSpan.FromTicks(g.Key * interval.Ticks), value, null, Abstractions.QualityCode.Good);
            })
            .ToArray();
    }
}
