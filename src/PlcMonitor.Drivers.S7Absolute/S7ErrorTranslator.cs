using System.Globalization;
using System.Text.RegularExpressions;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.S7Absolute;

/// <summary>
/// Traduce los fallos de lectura/escritura a calidades y mensajes útiles.
/// S7.Net no interpreta la cabecera de error de S7comm: cuando el PLC rechaza la petición, solo informa de que la
/// respuesta es más corta de lo esperado e incluye sus bytes. De ahí se sacan la clase y el código de error.
/// </summary>
internal static partial class S7ErrorTranslator
{
    /// <summary>Clase 0x81 / código 0x04: la CPU no admite el servicio en este contexto (en S7-1200/1500, PUT/GET deshabilitado).</summary>
    internal const ushort FunctionNotAvailable = 0x8104;

    public static (QualityCode Quality, string Detail) Translate(Exception exception, QualityCode fallback)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (TryGetHeaderError(ex.Message, out var error))
                return Describe(error);
        }
        return (fallback, exception.Message);
    }

    /// <summary>Busca en el texto una respuesta S7comm (32 02/03 ...) y devuelve su clase y código de error.</summary>
    internal static bool TryGetHeaderError(string message, out ushort error)
    {
        error = 0;
        var match = ResponseBytes().Match(message);
        if (!match.Success)
            return false;
        var bytes = match.Groups[1].Value.Split('-').Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        // Cabecera ACK_DATA/ACK: protocolo 0x32, tipo 2 o 3, ..., clase de error (byte 10), código (byte 11).
        if (bytes.Length < 12 || bytes[0] != 0x32 || bytes[1] is not (2 or 3))
            return false;
        error = (ushort)((bytes[10] << 8) | bytes[11]);
        return error != 0;
    }

    private static (QualityCode, string) Describe(ushort error) => error switch
    {
        FunctionNotAvailable => (QualityCode.BadAccessDenied,
            "El PLC rechaza el acceso (error S7 0x8104). En S7-1200/1500 hay que habilitar «Permitir acceso vía comunicación PUT/GET» " +
            "en Protección y seguridad → Mecanismos de conexión, cargar la configuración y, si aplica, que el DB no sea de acceso optimizado."),
        0x8500 => (QualityCode.Bad, "Error S7 0x8500: la petición supera el tamaño de PDU negociado."),
        0xD602 or 0xD603 => (QualityCode.BadAccessDenied, $"Error S7 0x{error:X4}: protección por contraseña de la CPU."),
        _ => (QualityCode.Bad, $"El PLC devolvió el error S7 0x{error:X4}."),
    };

    [GeneratedRegex(@"'((?:[0-9A-Fa-f]{2}-){11,}[0-9A-Fa-f]{2})'")]
    private static partial Regex ResponseBytes();
}
