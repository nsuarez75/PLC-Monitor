using System.Collections;
using System.Globalization;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Core;

/// <summary>Representación textual de valores para mostrar en CLI/UI (cultura invariante).</summary>
public static class ValueFormatter
{
    public const int MaxArrayItems = 32;

    public static string Format(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString(dt.Kind == DateTimeKind.Utc ? "yyyy-MM-dd HH:mm:ss.fffZ" : "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 256))) + (bytes.Length > 256 ? "…" : string.Empty),
        IEnumerable items => FormatSequence(items),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static string Format(TagValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Quality.IsBad()
            ? $"<{value.Quality}{(value.ErrorDetail is null ? string.Empty : ": " + value.ErrorDetail)}>"
            : Format(value.Value);
    }

    private static string FormatSequence(IEnumerable items)
    {
        var parts = new List<string>();
        var total = 0;
        foreach (var item in items)
        {
            // Los textos van entre comillas para que se vean los vacíos: ["hola", "", ""].
            if (total++ < MaxArrayItems)
                parts.Add(item is string s ? $"\"{s}\"" : Format(item));
        }
        var suffix = total > MaxArrayItems ? $", … ({total} elementos)" : string.Empty;
        return "[" + string.Join(", ", parts) + suffix + "]";
    }
}
