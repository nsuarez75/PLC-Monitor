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

    /// <summary>Nombre del agente para la cabecera de las imágenes exportadas («Agente planta»).</summary>
    string AgentTitle => "";

    /// <summary>Describe un error de la API para el usuario.</summary>
    string Describe(Exception exception);

    /// <summary>Transformación de valores del agente por su nombre; null si no existe.</summary>
    ValueTransform? FindTransform(string name) => null;

    /// <summary>El grupo de visualización <paramref name="folder"/> de un dispositivo está plegado en la tabla de tags.</summary>
    bool IsFolderCollapsed(string device, string folder) => false;

    /// <summary>Recuerda si un grupo de visualización está plegado.</summary>
    void SetFolderCollapsed(string device, string folder, bool collapsed)
    {
    }
}
