using S7CommPlusDriver;

namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>Error de la librería S7CommPlus (código numérico de S7Consts).</summary>
public sealed class S7SymbolicException : Exception
{
    public S7SymbolicException()
    {
    }

    public S7SymbolicException(string message)
        : base(message)
    {
    }

    public S7SymbolicException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public S7SymbolicException(string operation, int code, bool isCommunicationError = false, Exception? innerException = null)
        : base($"{operation}: {Describe(code)} (0x{code:X8}){Detail(innerException)}", innerException)
    {
        Code = code;
        IsCommunicationError = isCommunicationError || IsCommunicationCode(code);
    }

    public int Code { get; }

    /// <summary>Errores de TCP, ISO-on-TCP, timeouts, TLS o integridad: la sesión ya no es utilizable.</summary>
    public bool IsCommunicationError { get; }

    public static bool IsCommunicationCode(int code) =>
        (code & 0xFFFF) != 0 && code < S7Consts.errIsoConnect  // errTCP* (incluye 0x2751 host inalcanzable)
        || (code >= S7Consts.errIsoConnect && code <= S7Consts.errIsoInvalidDataSize)
        || code == S7Consts.errCliJobTimeout
        || code == S7Consts.errOpenSSL
        || code == S7Consts.errS7CommPlusDigestMismatch;

    // Detalle de la librería (en inglés): causa original de la excepción que se envuelve.
    private static string Detail(Exception? innerException)
    {
        var root = innerException;
        while (root?.InnerException is not null)
            root = root.InnerException;
        return root is null ? string.Empty : $" — {root.Message}";
    }

    public static string Describe(int code) => code switch
    {
        S7Consts.errOpenSSL => "Error en el canal TLS",
        S7Consts.errInitSslResponse => "La CPU no acepta comunicación segura (TLS). Usa security \"auto\" o \"legacy\"",
        S7Consts.errS7CommPlusLegacyAuthentication => "La CPU rechazó la autenticación clásica (sin TLS): clave pública no reconocida o CPU que exige TLS",
        S7Consts.errS7CommPlusDigestMismatch => "Firma de integridad incorrecta en la respuesta de la CPU",
        S7Consts.errCliNeedPassword => "La CPU necesita contraseña",
        S7Consts.errCliInvalidPassword => "Contraseña incorrecta",
        S7Consts.errCliAccessDenied => "Acceso denegado (¿contraseña incorrecta?)",
        S7Consts.errCliItemNotAvailable => "Elemento no disponible en la CPU",
        _ => S7Client.ErrorText(code),
    };
}
