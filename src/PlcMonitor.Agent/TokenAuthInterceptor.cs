using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;
using PlcMonitor.Agent.Contracts.Client;

namespace PlcMonitor.Agent;

/// <summary>Seguridad del agente (sección "Agent:Security", normalmente en agent.settings.json).</summary>
public sealed class AgentSecurityOptions
{
    /// <summary>SHA-256 (hex) del token de acceso. Vacío = la API no exige token (solo para uso local).</summary>
    public string TokenSha256 { get; set; } = "";
}

/// <summary>Exige <c>authorization: Bearer &lt;token&gt;</c> en todas las llamadas si hay token configurado.</summary>
public sealed class TokenAuthInterceptor(IOptions<AgentSecurityOptions> options, ILogger<TokenAuthInterceptor> logger) : Interceptor
{
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
        var expected = options.Value.TokenSha256;
        if (string.IsNullOrWhiteSpace(expected))
            return;
        var header = context.RequestHeaders.GetValue(AgentSecurity.AuthorizationHeader);
        var token = header is not null && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : null;
        if (AgentSecurity.Matches(token, expected))
            return;
        logger.LogWarning("Llamada rechazada sin token válido desde {Peer} ({Method})", context.Peer, context.Method);
        throw new RpcException(new Status(StatusCode.Unauthenticated, "Token de acceso al agente ausente o incorrecto."));
    }
}
