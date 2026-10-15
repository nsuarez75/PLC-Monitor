using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Contracts.Client;

namespace PlcMonitor.App.Services;

/// <summary>Crea clientes gRPC del agente (en los tests, contra el agente en memoria).</summary>
public interface IAgentConnector
{
    (AgentService.AgentServiceClient Client, IDisposable Channel) Connect(AgentEndpoint endpoint);
}

/// <summary>Conexión real: token y certificado fijado si el perfil los tiene.</summary>
public sealed class GrpcAgentConnector : IAgentConnector
{
    public (AgentService.AgentServiceClient Client, IDisposable Channel) Connect(AgentEndpoint endpoint)
    {
        var channel = AgentChannelFactory.Create(endpoint);
        return (new AgentService.AgentServiceClient(channel), channel);
    }
}
