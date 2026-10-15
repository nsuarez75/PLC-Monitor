using PlcMonitor.Abstractions;
using PlcMonitor.Engine;

namespace PlcMonitor.Historian;

/// <summary>Muestra que se guarda en el histórico.</summary>
public sealed record HistorySample(TagKey Key, DateTimeOffset Timestamp, object? Value, TagDataType DataType, QualityCode Quality);

/// <summary>Punto leído del histórico. Los valores numéricos (y booleanos como 0/1) van en <see cref="Number"/>; el resto en <see cref="Text"/>.</summary>
public sealed record HistoryPoint(DateTimeOffset Timestamp, double? Number, string? Text, QualityCode Quality);

public enum HistoryAggregate
{
    Average,
    Min,
    Max,
    First,
    Last,
}

/// <summary>Consulta: tags de un dispositivo entre dos instantes, en bruto (<see cref="Interval"/> nulo) o agregados por intervalo.</summary>
public sealed record HistoryQuery(
    IReadOnlyList<TagKey> Tags,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? Interval = null,
    HistoryAggregate Aggregate = HistoryAggregate.Average,
    int MaxPoints = 10_000);

/// <summary>Serie de un tag.</summary>
public sealed record HistorySeries(TagKey Key, IReadOnlyList<HistoryPoint> Points, bool Truncated);

/// <summary>Estado del historiador.</summary>
public sealed record HistorianStats(bool Enabled, string Path, int RetentionDays, long SizeBytes, int Files, int HistorizedTags, long PendingSamples);
