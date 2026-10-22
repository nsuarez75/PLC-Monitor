using System.Globalization;
using System.Text.RegularExpressions;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.Modbus;

/// <summary>Tablas de datos de Modbus.</summary>
public enum ModbusTable
{
    /// <summary>Coils (bits de lectura/escritura, 0xxxx). Funciones 01, 05 y 15.</summary>
    Coil,

    /// <summary>Entradas discretas (bits de solo lectura, 1xxxx). Función 02.</summary>
    DiscreteInput,

    /// <summary>Registros de entrada (solo lectura, 3xxxx). Función 04.</summary>
    InputRegister,

    /// <summary>Registros de retención (lectura/escritura, 4xxxx). Funciones 03, 06 y 16.</summary>
    HoldingRegister,
}

public enum ModbusValueType
{
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    String,
}

/// <summary>
/// Dirección Modbus. Sintaxis aceptada (sin distinguir mayúsculas):
/// <code>
///   Por tabla:  HR (registros de retención) · IR (registros de entrada) · C (coils) · DI (entradas discretas) + número.
///               Empieza en 0, o en 1 si el dispositivo tiene OneBasedAddresses.
///   Modicon:    0xxxx/0xxxxx coils · 1xxxx entradas · 3xxxx registros de entrada · 4xxxx registros de retención (siempre desde 1).
///   Bit de un registro:  HR10.3 · 40011.3 (0 = el menos significativo, hasta 15).
///   Tipo (registros):    :INT16 (por defecto) · :UINT16 · :INT32 · :UINT32 · :INT64 · :UINT64 · :FLOAT · :DOUBLE · :STRING[n]
///   Orden de bytes:      :ABCD · :BADC · :CDAB · :DCBA (si no se indica, el del dispositivo).
///   Ejemplos: HR0 · HR100:FLOAT · HR100:FLOAT:CDAB · IR5:UINT16 · C0 · DI3 · HR10.3 · HR20:STRING[10] · 40001 · 400101:INT32
/// </code>
/// En STRING el número entre corchetes es la cantidad de caracteres (dos por registro).
/// </summary>
public sealed partial record ModbusAddress(
    ModbusTable Table,
    int Offset,
    ModbusValueType ValueType,
    int BitIndex = -1,
    ModbusByteOrder? ByteOrder = null,
    int StringLength = 0)
{
    /// <summary>Caracteres máximos de un texto (125 registros, lo que cabe en una lectura).</summary>
    public const int MaxStringLength = 250;

    [GeneratedRegex(@"^(?<table>HR|IR|DI|C)(?<num>\d{1,5})(?:\.(?<bit>\d{1,2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"^(?<lead>[0134])(?<num>\d{4,5})(?:\.(?<bit>\d{1,2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ModiconRegex();

    [GeneratedRegex(@"^STRING\[(?<len>\d{1,3})\]$", RegexOptions.CultureInvariant)]
    private static partial Regex StringRegex();

    /// <summary>Tabla de bits (coils o entradas discretas).</summary>
    public bool IsBitTable => Table is ModbusTable.Coil or ModbusTable.DiscreteInput;

    /// <summary>Bit dentro de un registro (HR10.3).</summary>
    public bool IsRegisterBit => BitIndex >= 0;

    /// <summary>Se puede escribir (coils y registros de retención).</summary>
    public bool IsWritable => Table is ModbusTable.Coil or ModbusTable.HoldingRegister;

    /// <summary>Registros (o bits, en las tablas de bits) que ocupa.</summary>
    public int Count => IsBitTable || IsRegisterBit ? 1 : ValueType switch
    {
        ModbusValueType.Int32 or ModbusValueType.UInt32 or ModbusValueType.Float32 => 2,
        ModbusValueType.Int64 or ModbusValueType.UInt64 or ModbusValueType.Float64 => 4,
        ModbusValueType.String => (StringLength + 1) / 2,
        _ => 1,
    };

    public TagDataType DataType => ValueType switch
    {
        ModbusValueType.Bool => TagDataType.Boolean,
        ModbusValueType.Int16 => TagDataType.Int16,
        ModbusValueType.UInt16 => TagDataType.UInt16,
        ModbusValueType.Int32 => TagDataType.Int32,
        ModbusValueType.UInt32 => TagDataType.UInt32,
        ModbusValueType.Int64 => TagDataType.Int64,
        ModbusValueType.UInt64 => TagDataType.UInt64,
        ModbusValueType.Float32 => TagDataType.Float,
        ModbusValueType.Float64 => TagDataType.Double,
        _ => TagDataType.String,
    };

    public static ModbusAddress Parse(string text, bool oneBased = false) =>
        TryParse(text, oneBased, out var address, out var error) ? address : throw new FormatException(error);

    /// <param name="oneBased">Las direcciones por tabla empiezan en 1 (ajuste del dispositivo).</param>
    public static bool TryParse(string? text, bool oneBased, out ModbusAddress address, out string error)
    {
        address = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Dirección vacía.";
            return false;
        }

        var parts = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant().Split(':');
        if (!TryParseLocation(text, parts[0], oneBased, out var table, out var offset, out var bit, out error))
            return false;

        ModbusValueType? type = null;
        ModbusByteOrder? order = null;
        var stringLength = 0;
        foreach (var part in parts.Skip(1))
        {
            if (part is "ABCD" or "BADC" or "CDAB" or "DCBA")
            {
                var o = Enum.Parse<ModbusByteOrder>(part);
                if (order is not null)
                {
                    error = $"'{text}': el orden de bytes aparece dos veces.";
                    return false;
                }
                order = o;
            }
            else if (TryParseType(part, out var t, out var length))
            {
                if (type is not null)
                {
                    error = $"'{text}': el tipo aparece dos veces.";
                    return false;
                }
                type = t;
                stringLength = length;
            }
            else
            {
                error = $"'{text}': «{part}» no es un tipo (INT16, UINT16, INT32, UINT32, INT64, UINT64, FLOAT, DOUBLE, STRING[n], BOOL) " +
                    "ni un orden de bytes (ABCD, BADC, CDAB, DCBA).";
                return false;
            }
        }

        var isBitTable = table is ModbusTable.Coil or ModbusTable.DiscreteInput;
        if (isBitTable)
        {
            if (bit >= 0 || (type is not null && type != ModbusValueType.Bool) || order is not null)
            {
                error = $"'{text}': los coils y las entradas discretas son bits sueltos; no llevan bit, tipo ni orden de bytes.";
                return false;
            }
            type = ModbusValueType.Bool;
        }
        else if (bit >= 0)
        {
            if (bit > 15)
            {
                error = $"'{text}': el bit de un registro va de 0 a 15.";
                return false;
            }
            if ((type is not null && type != ModbusValueType.Bool) || order is not null)
            {
                error = $"'{text}': un bit de registro (p.ej. HR10.3) no lleva tipo ni orden de bytes.";
                return false;
            }
            type = ModbusValueType.Bool;
        }
        else if (type == ModbusValueType.Bool)
        {
            error = $"'{text}': para leer un bit de un registro indica cuál (p.ej. HR10.3).";
            return false;
        }

        if (type == ModbusValueType.String && stringLength is < 1 or > MaxStringLength)
        {
            error = $"'{text}': la longitud de un STRING debe estar entre 1 y {MaxStringLength}.";
            return false;
        }

        address = new ModbusAddress(table, offset, type ?? ModbusValueType.Int16, bit, order, stringLength);
        if (address.Offset + address.Count > 65536)
        {
            error = $"'{text}': el valor se sale del mapa Modbus (la última dirección es la 65535).";
            address = null!;
            return false;
        }
        return true;
    }

    /// <summary>Forma por tabla (HR100:FLOAT:CDAB), con la numeración del dispositivo.</summary>
    public string Format(bool oneBased = false)
    {
        var prefix = Table switch
        {
            ModbusTable.Coil => "C",
            ModbusTable.DiscreteInput => "DI",
            ModbusTable.InputRegister => "IR",
            _ => "HR",
        };
        var s = string.Create(CultureInfo.InvariantCulture, $"{prefix}{Offset + (oneBased ? 1 : 0)}");
        if (IsRegisterBit)
            return s + string.Create(CultureInfo.InvariantCulture, $".{BitIndex}");
        if (IsBitTable)
            return s;
        if (ValueType != ModbusValueType.Int16)
        {
            s += ValueType switch
            {
                ModbusValueType.Float32 => ":FLOAT",
                ModbusValueType.Float64 => ":DOUBLE",
                ModbusValueType.String => string.Create(CultureInfo.InvariantCulture, $":STRING[{StringLength}]"),
                _ => ":" + ValueType.ToString().ToUpperInvariant(),
            };
        }
        if (ByteOrder is { } order)
            s += ":" + order;
        return s;
    }

    public override string ToString() => Format();

    private static bool TryParseLocation(
        string text, string location, bool oneBased, out ModbusTable table, out int offset, out int bit, out string error)
    {
        table = default;
        offset = 0;
        bit = -1;
        error = string.Empty;

        var m = TableRegex().Match(location);
        if (m.Success)
        {
            table = m.Groups["table"].Value switch
            {
                "HR" => ModbusTable.HoldingRegister,
                "IR" => ModbusTable.InputRegister,
                "DI" => ModbusTable.DiscreteInput,
                _ => ModbusTable.Coil,
            };
            var number = Int(m.Groups["num"].Value);
            offset = oneBased ? number - 1 : number;
            if (offset is < 0 or > 65535)
            {
                error = oneBased
                    ? $"'{text}': el dispositivo numera desde 1; la dirección debe estar entre 1 y 65536."
                    : $"'{text}': la dirección debe estar entre 0 y 65535.";
                return false;
            }
        }
        else if ((m = ModiconRegex().Match(location)).Success)
        {
            table = m.Groups["lead"].Value switch
            {
                "0" => ModbusTable.Coil,
                "1" => ModbusTable.DiscreteInput,
                "3" => ModbusTable.InputRegister,
                _ => ModbusTable.HoldingRegister,
            };
            // 40001 = primer registro de retención (offset 0); con seis cifras (400001) caben hasta 65536.
            offset = Int(m.Groups["num"].Value) - 1;
            if (offset is < 0 or > 65535)
            {
                error = $"'{text}': en estilo Modicon la numeración empieza en 1 (p.ej. 40001 es el primer registro).";
                return false;
            }
        }
        else
        {
            error = $"'{text}' no es una dirección Modbus válida (ejemplos: HR0, HR100:FLOAT, IR5, C0, DI3, HR10.3, 40001).";
            return false;
        }

        if (m.Groups["bit"].Success)
            bit = Int(m.Groups["bit"].Value);
        return true;
    }

    private static bool TryParseType(string part, out ModbusValueType type, out int stringLength)
    {
        stringLength = 0;
        var s = StringRegex().Match(part);
        if (s.Success)
        {
            type = ModbusValueType.String;
            stringLength = Int(s.Groups["len"].Value);
            return true;
        }
        ModbusValueType? parsed = part switch
        {
            "BOOL" or "BIT" => ModbusValueType.Bool,
            "INT16" or "INT" or "SHORT" => ModbusValueType.Int16,
            "UINT16" or "UINT" or "WORD" => ModbusValueType.UInt16,
            "INT32" or "DINT" => ModbusValueType.Int32,
            "UINT32" or "UDINT" or "DWORD" => ModbusValueType.UInt32,
            "INT64" or "LINT" => ModbusValueType.Int64,
            "UINT64" or "ULINT" or "LWORD" => ModbusValueType.UInt64,
            "FLOAT" or "FLOAT32" or "REAL" => ModbusValueType.Float32,
            "DOUBLE" or "FLOAT64" or "LREAL" => ModbusValueType.Float64,
            _ => null,
        };
        type = parsed ?? default;
        return parsed is not null;
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
}
