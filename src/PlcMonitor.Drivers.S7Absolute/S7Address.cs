using System.Globalization;
using System.Text.RegularExpressions;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.S7Absolute;

public enum S7Area
{
    Inputs,
    Outputs,
    Merker,
    DataBlock,
}

public enum S7ValueType
{
    Bit,
    Byte,
    Char,
    Word,
    Int,
    DWord,
    DInt,
    Real,
    LWord,
    LInt,
    LReal,
    String,
    DateAndTime,
}

/// <summary>
/// Dirección absoluta S7. Sintaxis aceptada (sin distinguir mayúsculas), compatible con la de Ignition y con la clásica de STEP 7:
/// <code>
///   Área:  I | E (entradas) · Q | A (salidas) · M (marcas) · DB&lt;n&gt;.
///   Tipo:  X (bit) · B (Byte) · C (Char) · W (Word) · I|INT (Int) · D (DWord) · DI|DINT (DInt) · REAL
///          L (LWord) · LI|LINT (LInt) · LREAL · DT (Date_And_Time) · STRING
///   Ejemplos: DB1.DBX0.3 · DB1.DBW10 · DB1.REAL20 · DB5.STRING30[20] · DB1.INT0[10] · M10.1 · MW20 · IB0 · QX4.0
/// </code>
/// En STRING el valor entre corchetes es la longitud máxima (por defecto 254); en el resto, el número de elementos (array).
/// </summary>
public sealed partial record S7Address(
    S7Area Area,
    int DbNumber,
    int ByteOffset,
    int BitIndex,
    S7ValueType ValueType,
    int StringLength = 0,
    int ArrayCount = 1)
{
    public const int DefaultStringLength = 254;
    public const int MaxArrayCount = 4096;

    [GeneratedRegex(
        @"^(?:(?<area>I|E|Q|A|M)|DB(?<db>\d{1,5})\.?)(?:DB)?(?<type>LREAL|REAL|STRING|DINT|LINT|INT|DI|LI|DT|X|B|C|W|I|D|L)?(?<offset>\d{1,6})(?:\.(?<bit>[0-7]))?(?:\[(?<count>\d{1,4})\])?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AddressRegex();

    public bool IsArray => ArrayCount > 1;

    /// <summary>Tamaño de un elemento en bytes.</summary>
    public int ElementSize => ValueType switch
    {
        S7ValueType.Bit or S7ValueType.Byte or S7ValueType.Char => 1,
        S7ValueType.Word or S7ValueType.Int => 2,
        S7ValueType.DWord or S7ValueType.DInt or S7ValueType.Real => 4,
        S7ValueType.LWord or S7ValueType.LInt or S7ValueType.LReal or S7ValueType.DateAndTime => 8,
        S7ValueType.String => StringLength + 2,
        _ => throw new InvalidOperationException($"Tipo no soportado: {ValueType}"),
    };

    /// <summary>Bytes que ocupa la dirección completa (todos los elementos).</summary>
    public int ByteLength => ElementSize * ArrayCount;

    public TagDataType DataType => ValueType switch
    {
        S7ValueType.Bit => TagDataType.Boolean,
        S7ValueType.Byte => ArrayCount > 1 ? TagDataType.ByteArray : TagDataType.Byte,
        S7ValueType.Char or S7ValueType.String => TagDataType.String,
        S7ValueType.Word => TagDataType.UInt16,
        S7ValueType.Int => TagDataType.Int16,
        S7ValueType.DWord => TagDataType.UInt32,
        S7ValueType.DInt => TagDataType.Int32,
        S7ValueType.Real => TagDataType.Float,
        S7ValueType.LWord => TagDataType.UInt64,
        S7ValueType.LInt => TagDataType.Int64,
        S7ValueType.LReal => TagDataType.Double,
        S7ValueType.DateAndTime => TagDataType.DateTime,
        _ => TagDataType.Unknown,
    };

    public static S7Address Parse(string text) =>
        TryParse(text, out var address, out var error) ? address : throw new FormatException(error);

    public static bool TryParse(string? text, out S7Address address, out string error)
    {
        address = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Dirección vacía.";
            return false;
        }

        var normalized = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var m = AddressRegex().Match(normalized);
        if (!m.Success)
        {
            error = $"'{text}' no es una dirección S7 válida (ejemplos: DB1.DBW10, DB1.REAL20, M10.1, IW64).";
            return false;
        }

        var area = m.Groups["db"].Success ? S7Area.DataBlock : m.Groups["area"].Value switch
        {
            "I" or "E" => S7Area.Inputs,
            "Q" or "A" => S7Area.Outputs,
            _ => S7Area.Merker,
        };
        var db = m.Groups["db"].Success ? Int(m.Groups["db"].Value) : 0;
        if (area == S7Area.DataBlock && db == 0)
        {
            error = "El número de DB debe ser mayor que 0.";
            return false;
        }

        var offset = Int(m.Groups["offset"].Value);
        var hasBit = m.Groups["bit"].Success;
        var bit = hasBit ? Int(m.Groups["bit"].Value) : 0;
        int? bracket = m.Groups["count"].Success ? Int(m.Groups["count"].Value) : null;

        S7ValueType type;
        if (!m.Groups["type"].Success)
        {
            if (!hasBit)
            {
                error = $"'{text}': falta el tipo de dato (p.ej. B, W, INT, REAL) o el bit (.0-.7).";
                return false;
            }
            type = S7ValueType.Bit;
        }
        else
        {
            type = m.Groups["type"].Value switch
            {
                "X" => S7ValueType.Bit,
                "B" => S7ValueType.Byte,
                "C" => S7ValueType.Char,
                "W" => S7ValueType.Word,
                "I" or "INT" => S7ValueType.Int,
                "D" => S7ValueType.DWord,
                "DI" or "DINT" => S7ValueType.DInt,
                "REAL" => S7ValueType.Real,
                "L" => S7ValueType.LWord,
                "LI" or "LINT" => S7ValueType.LInt,
                "LREAL" => S7ValueType.LReal,
                "DT" => S7ValueType.DateAndTime,
                _ => S7ValueType.String,
            };
        }

        if (type == S7ValueType.Bit && !hasBit)
        {
            error = $"'{text}': un bit necesita índice (p.ej. DB1.DBX0.3).";
            return false;
        }
        if (type != S7ValueType.Bit && hasBit)
        {
            error = $"'{text}': solo las direcciones de bit (X) llevan índice de bit.";
            return false;
        }

        var stringLength = 0;
        var count = 1;
        if (type == S7ValueType.String)
        {
            stringLength = bracket ?? DefaultStringLength;
            if (stringLength is < 1 or > 254)
            {
                error = $"'{text}': la longitud de un STRING debe estar entre 1 y 254.";
                return false;
            }
        }
        else if (bracket is { } n)
        {
            if (type is S7ValueType.Bit or S7ValueType.DateAndTime or S7ValueType.Char)
            {
                error = $"'{text}': no se admiten arrays de {type}.";
                return false;
            }
            if (n is < 1 or > MaxArrayCount)
            {
                error = $"'{text}': el número de elementos debe estar entre 1 y {MaxArrayCount}.";
                return false;
            }
            count = n;
        }

        address = new S7Address(area, db, offset, bit, type, stringLength, count);
        return true;
    }

    /// <summary>Forma canónica (estilo Ignition).</summary>
    public override string ToString()
    {
        var prefix = Area switch
        {
            S7Area.DataBlock => $"DB{DbNumber}.",
            S7Area.Inputs => "I",
            S7Area.Outputs => "Q",
            _ => "M",
        };
        var type = ValueType switch
        {
            S7ValueType.Bit => "X",
            S7ValueType.Byte => "B",
            S7ValueType.Char => "C",
            S7ValueType.Word => "W",
            S7ValueType.Int => "INT",
            S7ValueType.DWord => "D",
            S7ValueType.DInt => "DINT",
            S7ValueType.Real => "REAL",
            S7ValueType.LWord => "L",
            S7ValueType.LInt => "LINT",
            S7ValueType.LReal => "LREAL",
            S7ValueType.DateAndTime => "DT",
            _ => "STRING",
        };
        var s = string.Create(CultureInfo.InvariantCulture, $"{prefix}{type}{ByteOffset}");
        if (ValueType == S7ValueType.Bit)
            s += string.Create(CultureInfo.InvariantCulture, $".{BitIndex}");
        if (ValueType == S7ValueType.String)
            s += string.Create(CultureInfo.InvariantCulture, $"[{StringLength}]");
        else if (ArrayCount > 1)
            s += string.Create(CultureInfo.InvariantCulture, $"[{ArrayCount}]");
        return s;
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
}
