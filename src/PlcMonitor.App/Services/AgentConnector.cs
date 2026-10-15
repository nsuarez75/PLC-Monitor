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
