using System.Security.Authentication;
using Grpc.Core;

namespace PlcMonitor.Agent.Contracts.Client;

/// <summary>Mensajes legibles para los errores al hablar con un agente.</summary>
public static class AgentErrors
{
    /// <summary>El certificado del agente no es el fijado (otro equipo o agente reinstalado).</summary>
    public static bool IsCertificateMismatch(Exception exception)
    {
        for (Exception? e = (exception as RpcException)?.Status.DebugException ?? exception; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return true;
        }
        return false;
    }

    public static string Describe(Exception exception, string url) => exception switch
    {
        _ when IsCertificateMismatch(exception) =>
            $"El certificado del colector en {url} no es el esperado (¿otro equipo o colector reinstalado?). Revisa el perfil del colector.",
        RpcException { StatusCode: StatusCode.Unauthenticated } => $"El colector en {url} rechaza el token de acceso. Revisa el perfil del colector.",
        RpcException { StatusCode: StatusCode.Unavailable } => $"No se puede conectar con el colector en {url}. ¿Está arrancado?",
        RpcException { StatusCode: StatusCode.DeadlineExceeded } => $"El colector en {url} no responde a tiempo.",
        RpcException { StatusCode: StatusCode.Unimplemented } => $"{url} no es un colector de PLC-Monitor (o es de otra versión).",
        RpcException rpc => string.IsNullOrEmpty(rpc.Status.Detail) ? rpc.StatusCode.ToString() : rpc.Status.Detail,
        UriFormatException or InvalidOperationException => $"URL no válida: {url}",
        _ => exception.Message,
    };
}
