using Grpc.Core;
using Grpc.Net.Client;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.Services;

/// <summary>Crea clientes gRPC del agente (en los tests, contra el agente en memoria).</summary>
public interface IAgentConnector
{
    (AgentService.AgentServiceClient Client, IDisposable Channel) Connect(string url);
}

public sealed class GrpcAgentConnector : IAgentConnector
{
    public (AgentService.AgentServiceClient Client, IDisposable Channel) Connect(string url)
    {
        var channel = GrpcChannel.ForAddress(url);
        return (new AgentService.AgentServiceClient(channel), channel);
    }
}

/// <summary>Mensajes legibles para los errores gRPC.</summary>
public static class AgentErrors
{
    public static string Describe(Exception ex, string url) => ex switch
    {
        RpcException { StatusCode: StatusCode.Unavailable } => $"No se puede conectar con el agente en {url}. ¿Está arrancado?",
        RpcException { StatusCode: StatusCode.DeadlineExceeded } => $"El agente en {url} no responde a tiempo.",
        RpcException { StatusCode: StatusCode.Unimplemented } => $"{url} no es un agente de PLC-Monitor (o es de otra versión).",
        RpcException rpc => string.IsNullOrEmpty(rpc.Status.Detail) ? rpc.StatusCode.ToString() : rpc.Status.Detail,
        UriFormatException or InvalidOperationException => $"URL no válida: {url}",
        _ => ex.Message,
    };
}
