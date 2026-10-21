using System.Globalization;
using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Engine;

/// <summary>Aplica el escalado de un tag a los valores leídos y su inversa a los escritos.</summary>
internal static class ValueScaling
{
    /// <summary>
    /// Valor en unidades de ingeniería (Double) con el bruto en <see cref="TagValue.Raw"/>. Los valores malos y los que no
    /// son un número escalar (booleanos, textos, arrays...) se dejan como están.
    /// </summary>
    public static TagValue Apply(TagScaling? scaling, TagValue value)
    {
        if (scaling is null || value.Quality.IsBad() || !TryNumber(value.Value, out var raw))
            return value;
        return value with { Value = scaling.ToEngineering(raw), DataType = TagDataType.Double, Raw = value };
    }

    /// <summary>
    /// Convierte el texto escrito en unidades de ingeniería al texto del valor bruto del tipo <paramref name="rawType"/>.
    /// Los enteros se redondean (1.5 → 2); un valor que no cabe en el tipo lo rechaza después el parser.
    /// </summary>
    public static bool TryToRaw(TagScaling scaling, string text, TagDataType rawType, out string rawText, out string? error)
    {
        rawText = "";
        if (!ValueParser.TryParse(text, TagDataType.Double, out var parsed, out error))
            return false;
        var engineering = (double)parsed!;
        if (scaling.Clamp && !scaling.IsInRange(engineering))
        {
            var min = Math.Min(scaling.EngMin, scaling.EngMax).ToString("R", CultureInfo.InvariantCulture);
            var max = Math.Max(scaling.EngMin, scaling.EngMax).ToString("R", CultureInfo.InvariantCulture);
            error = $"El valor está fuera del rango de ingeniería del tag ({min} a {max}).";
            return false;
        }
        var raw = scaling.ToRaw(engineering);
        rawText = IsInteger(rawType)
            ? Math.Round(raw, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)
            : raw.ToString("R", CultureInfo.InvariantCulture);
        return true;
    }

    public static bool IsNumeric(TagDataType type) => IsInteger(type) || type is TagDataType.Float or TagDataType.Double;

    private static bool IsInteger(TagDataType type) => type is TagDataType.SByte or TagDataType.Byte or TagDataType.Int16 or TagDataType.UInt16
        or TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Int64 or TagDataType.UInt64;

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            // Por el texto, para que 0.1f dé 0.1 y no 0.100000001.
            case float f:
                number = double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                return true;
            case sbyte or byte or short or ushort or int or uint or long or ulong or double:
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            default:
                number = 0;
                return false;
        }
    }
}
