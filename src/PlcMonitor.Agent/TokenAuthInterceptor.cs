using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;
using PlcMonitor.Agent.Contracts.Client;

namespace PlcMonitor.Agent;

/// <summary>Seguridad del agente (sección "Agent:Security", normalmente en agent.settings.json).</summary>
public sealed class AgentSecurityOptions
{
    /// <summary>SHA-256 (hex) del token de acceso completo. Vacío y sin token de lectura = la API no exige token (solo para uso local).</summary>
    public string TokenSha256 { get; set; } = "";

    /// <summary>SHA-256 (hex) del token de solo lectura (opcional): leer, suscribirse, explorar y consultar, sin cambiar nada.</summary>
    public string ReadOnlyTokenSha256 { get; set; } = "";

    /// <summary>Hay algún token configurado: las llamadas sin token se rechazan.</summary>
    public bool RequiresToken => !string.IsNullOrWhiteSpace(TokenSha256) || !string.IsNullOrWhiteSpace(ReadOnlyTokenSha256);
}

/// <summary>Lo que puede hacer quien llama.</summary>
public enum AgentAccess
{
    /// <summary>Solo lectura: leer, suscribirse, explorar, ver la configuración y el histórico.</summary>
    Read,

    /// <summary>Todo: además escribir en el PLC, configurar y borrar o renombrar histórico.</summary>
    Full,
}

/// <summary>
/// Exige <c>authorization: Bearer &lt;token&gt;</c> en todas las llamadas si hay token configurado y deja el acceso de quien
/// llama en el contexto (<see cref="AgentCaller"/>). Con el token de lectura, las llamadas que cambian algo se rechazan.
/// </summary>
public sealed class TokenAuthInterceptor(IOptions<AgentSecurityOptions> options, ILogger<TokenAuthInterceptor> logger) : Interceptor
{
    /// <summary>Métodos que cambian algo (en el PLC, la configuración o el histórico): solo con acceso completo.</summary>
    internal static readonly HashSet<string> FullAccessMethods = new(StringComparer.Ordinal)
    {
        "Write", "SetDeviceEnabled", "PutDevice", "DeleteDevice", "ReorderDevices", "PutTransforms", "RenameHistory", "DeleteHistory",
        "PutOpcUa", "TrustOpcUaCertificate", "DeleteOpcUaCertificate",
    };

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, responseStream, context);
    }

    private void Authorize(ServerCallContext context)
    {
        var security = options.Value;
        var caller = Identify(context, security);
        if (caller is null)
        {
            logger.LogWarning("Llamada rechazada sin token válido desde {Peer} ({Method})", context.Peer, context.Method);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Token de acceso al colector ausente o incorrecto."));
        }
        context.UserState[AgentCaller.Key] = caller;
        var method = context.Method[(context.Method.LastIndexOf('/') + 1)..];
        if (caller.Access == AgentAccess.Full || !FullAccessMethods.Contains(method))
            return;
        logger.LogWarning("Llamada {Method} rechazada: {Peer} usa el token de solo lectura", method, context.Peer);
        throw new RpcException(new Status(StatusCode.PermissionDenied, $"El token de solo lectura no permite {method}."));
    }

    /// <summary>Quién llama según el token; null si hace falta token y no es válido.</summary>
    private static AgentCaller? Identify(ServerCallContext context, AgentSecurityOptions security)
    {
        if (!security.RequiresToken)
            return AgentCaller.Local;
        var header = context.RequestHeaders.GetValue(AgentSecurity.AuthorizationHeader);
        var token = header is not null && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : null;
        if (AgentSecurity.Matches(token, security.TokenSha256))
            return AgentCaller.FullToken;
        if (AgentSecurity.Matches(token, security.ReadOnlyTokenSha256))
            return AgentCaller.ReadOnlyToken;
        return null;
    }
}

/// <summary>Quién llama: acceso y con qué se identificó (para la auditoría).</summary>
/// <param name="Access">Lo que puede hacer.</param>
/// <param name="Credential">Cómo entró: «sin token», «token completo» o «token de lectura».</param>
public sealed record AgentCaller(AgentAccess Access, string Credential)
{
    internal const string Key = "plcmon.caller";

    public static readonly AgentCaller Local = new(AgentAccess.Full, "sin token");
    public static readonly AgentCaller FullToken = new(AgentAccess.Full, "token completo");
    public static readonly AgentCaller ReadOnlyToken = new(AgentAccess.Read, "token de lectura");

    /// <summary>El que dejó el interceptor en el contexto (sin interceptor, como en un agente local: acceso completo).</summary>
    public static AgentCaller From(ServerCallContext context) =>
        context.UserState.TryGetValue(Key, out var caller) && caller is AgentCaller known ? known : Local;

    /// <summary>
    /// Texto para la auditoría: el usuario que dice el cliente (no se puede comprobar), desde dónde llama y con qué
    /// credencial entró, que es lo único verificable. P.ej. «nacho@ipv4:10.0.0.5:51234 (token completo)».
    /// </summary>
    public static string Describe(ServerCallContext context, string? declaredUser = null)
    {
        var user = string.IsNullOrWhiteSpace(declaredUser) ? "anónimo" : declaredUser.Trim();
        return $"{user}@{context.Peer} ({From(context).Credential})";
    }
}
