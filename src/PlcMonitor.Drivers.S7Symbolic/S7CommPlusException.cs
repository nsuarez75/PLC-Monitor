using S7CommPlusDriver;

namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>Error devuelto por la librería S7CommPlus (código numérico de S7Consts).</summary>
public sealed class S7CommPlusException : Exception
{
    public S7CommPlusException()
    {
    }

    public S7CommPlusException(string message)
        : base(message)
    {
    }

    public S7CommPlusException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public S7CommPlusException(string operation, int code)
        : base($"{operation}: {Describe(code)} (0x{code:X8})")
    {
        Code = code;
    }

    public int Code { get; }

    /// <summary>Errores de TCP, ISO-on-TCP, timeouts o TLS: la sesión ya no es utilizable.</summary>
    public bool IsCommunicationError => IsCommunicationCode(Code);

    public static bool IsCommunicationCode(int code) =>
        (code & 0xFFFF) != 0                                  // errTCP* (incluye 0x2751 host inalcanzable)
        || (code >= S7Consts.errIsoConnect && code <= S7Consts.errIsoInvalidDataSize)
        || code == S7Consts.errCliJobTimeout
        || code == S7Consts.errOpenSSL;

    public static string Describe(int code) => code switch
    {
        S7Consts.errOpenSSL => "Error de OpenSSL/TLS (¿falta libssl-3/libcrypto-3 o la CPU no admite comunicación segura?)",
        S7Consts.errSecureCommNotAvailable => "La CPU no acepta comunicación segura (TLS). s7-symbolic solo funciona con CPU que la tengan activada " +
            "(S7-1200 FW ≥ V4.5 / S7-1500 FW ≥ V2.9 con proyecto TIA ≥ V17 y comunicación PG/HMI segura). Usa s7-absolute (PUT/GET)",
        S7Consts.errCliNeedPassword => "La CPU necesita contraseña",
        S7Consts.errCliInvalidPassword => "Contraseña incorrecta",
        S7Consts.errCliAccessDenied => "Acceso denegado (¿contraseña incorrecta?)",
        _ => S7Client.ErrorText(code),
    };
}
