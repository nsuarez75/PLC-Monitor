using System.Globalization;
using System.Text.RegularExpressions;

namespace PlcMonitor.Agent.Contracts.Client;

/// <summary>
/// Presentación de los valores con la unidad y los decimales del tag. Solo cambia cómo se ven: los decimales se aplican
/// a los reales (Float, Double) y la unidad a todos los números; booleanos, textos y fechas quedan tal cual.
/// </summary>
public static partial class ValueDisplay
{
    public static bool IsNumeric(DataType type) => type is DataType.SByte or DataType.Byte or DataType.Int16 or DataType.UInt16
        or DataType.Int32 or DataType.UInt32 or DataType.Int64 or DataType.UInt64 or DataType.Float or DataType.Double;

    public static bool IsReal(DataType type) => type is DataType.Float or DataType.Double;

    public static int? GetDecimals(this Tag tag) => tag.HasDecimals ? tag.Decimals : null;

    public static int? GetDecimals(this TagConfig tag) => tag.HasDecimals ? tag.Decimals : null;

    /// <summary>Texto del valor (el de <see cref="TagValue.Text"/>, cultura invariante) con decimales y unidad.</summary>
    public static string Format(string text, DataType type, int? decimals, string? unit)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsNumeric(type) || text.Length == 0)
            return text;
        if (IsReal(type) && decimals is { } d)
        {
            text = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? Number(value, d)
                : ArrayElement().Replace(text, m => double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Number(v, d) : m.Value);
        }
        return WithUnit(text, unit);
    }

    /// <summary>Número con <paramref name="decimals"/> decimales fijos, redondeando como un HMI (1,25 → 1,3) y sin «-0,00».</summary>
    public static string Number(double value, int decimals)
    {
        decimals = Math.Clamp(decimals, 0, 15);
        if (double.IsFinite(value))
            value = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (value == 0)
            value = 0;
        return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    public static string WithUnit(string text, string? unit) => string.IsNullOrWhiteSpace(unit) || text.Length == 0 ? text : $"{text} {unit.Trim()}";

    /// <summary>Nombre con la unidad entre corchetes, para cabeceras y leyendas: «presion [bar]».</summary>
    public static string Label(string name, string? unit) => string.IsNullOrWhiteSpace(unit) ? name : $"{name} [{unit.Trim()}]";

    // Elementos numéricos de un array tal como lo escribe el agente: «[1.5, -2E-05, NaN]»; no toca el «(40 elementos)» final.
    [GeneratedRegex(@"(?<=[\[ ])(-?\d+(\.\d+)?(E[+-]?\d+)?|NaN|-?Infinity)(?=,|\])", RegexOptions.CultureInvariant)]
    private static partial Regex ArrayElement();
}
