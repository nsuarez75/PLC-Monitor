namespace PlcMonitor.Abstractions;

/// <summary>Valor leído de un dispositivo con su calidad y marca de tiempo.</summary>
/// <param name="Value">Valor convertido a un tipo .NET (bool, int, float, string, DateTime, byte[]...). null si la calidad es mala.</param>
/// <param name="DataType">Tipo normalizado del valor.</param>
/// <param name="Quality">Calidad.</param>
/// <param name="Timestamp">Momento en que se obtuvo el valor (UTC).</param>
/// <param name="ErrorDetail">Descripción del error cuando la calidad no es buena.</param>
public sealed record TagValue(
    object? Value,
    TagDataType DataType,
    QualityCode Quality,
    DateTimeOffset Timestamp,
    string? ErrorDetail = null)
{
    public static TagValue Good(object? value, TagDataType dataType, DateTimeOffset timestamp) =>
        new(value, dataType, QualityCode.Good, timestamp);

    public static TagValue Bad(QualityCode quality, DateTimeOffset timestamp, string? detail = null) =>
        new(null, TagDataType.Unknown, quality, timestamp, detail);

    /// <summary>Compara valor y calidad, sin la marca de tiempo. Sirve para detectar cambios.</summary>
    public bool HasSameValueAndQuality(TagValue? other)
    {
        if (other is null || Quality != other.Quality)
            return false;
        return ValueEquals(Value, other.Value);
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a is byte[] ba && b is byte[] bb)
            return ba.AsSpan().SequenceEqual(bb);
        if (a is Array aa && b is Array ab)
            return aa.Length == ab.Length && aa.Cast<object?>().SequenceEqual(ab.Cast<object?>());
        return Equals(a, b);
    }
}
