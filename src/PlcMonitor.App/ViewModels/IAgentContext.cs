using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.ViewModels;

/// <summary>Lo que necesitan los ViewModels hijos de la sesión con el agente.</summary>
public interface IAgentContext
{
    /// <summary>Cliente del agente; null si no hay conexión.</summary>
    AgentService.AgentServiceClient? Client { get; }

    IDialogs Dialogs { get; }

    /// <summary>Muestra un mensaje en la barra de estado.</summary>
    void Report(string message, bool isError = false);

    /// <summary>Describe un error de la API para el usuario.</summary>
    string Describe(Exception exception);
}
