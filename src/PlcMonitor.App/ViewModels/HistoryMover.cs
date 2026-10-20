using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Al renombrar un tag o un dispositivo, su histórico se queda con el nombre anterior (el agente lo guarda por nombre):
/// se ofrece llevarlo al nombre nuevo.
/// </summary>
internal static class HistoryMover
{
    /// <summary>Pregunta y, si se acepta, pide al agente que mueva el histórico. <paramref name="what"/>: «el tag «a»», «el dispositivo «b»».</summary>
    public static async Task OfferAsync(IAgentContext context, AgentService.AgentServiceClient client, HistoryRename rename, string what, string newName)
    {
        if (!await context.Dialogs.ConfirmAsync("Histórico",
                $"El histórico de {what} está guardado con el nombre anterior.\n¿Llevarlo a «{newName}»? Si no, seguirá con el nombre anterior y la app no lo mostrará."))
            return;
        try
        {
            var reply = await client.RenameHistoryAsync(new RenameHistoryRequest { Renames = { rename } });
            context.Report(reply.Moved == 0 ? $"No había histórico de {what}" : $"Histórico llevado a «{newName}»");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            context.Report("Este agente no sabe mover el histórico: actualízalo desde «Agentes» (Instalar / actualizar).", isError: true);
        }
        catch (RpcException ex)
        {
            context.Report($"No se pudo mover el histórico: {context.Describe(ex)}", isError: true);
        }
    }
}
