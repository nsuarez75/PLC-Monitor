using Grpc.Net.Client;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Tests;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.Tests;

/// <summary>Ejecuta las acciones "de la UI" bajo un candado: los tests leen el modelo con <see cref="Read{T}"/>.</summary>
internal sealed class LockingDispatcher : IUiDispatcher
{
    private readonly Lock _gate = new();

    public void Post(Action action)
    {
        lock (_gate)
            action();
    }

    public T Read<T>(Func<T> read)
    {
        lock (_gate)
            return read();
    }
}

/// <summary>Diálogos con respuesta programada; guarda las preguntas.</summary>
internal sealed class FakeDialogs : IDialogs
{
    public bool Answer { get; set; } = true;

    public List<string> Questions { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Questions.Add(message);
        return Task.FromResult(Answer);
    }

    public List<PlcMonitor.App.ViewModels.TagDetailsViewModel> TagDetails { get; } = [];

    /// <summary>Ruta que devuelve el diálogo de guardar (null = cancelar).</summary>
    public string? SavePath { get; set; }

    public Task<string?> SaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult(SavePath);

    public Task ShowTagDetailsAsync(PlcMonitor.App.ViewModels.TagDetailsViewModel details)
    {
        TagDetails.Add(details);
        return Task.CompletedTask;
    }

    /// <summary>Ruta que devuelve el diálogo de abrir (null = cancelar).</summary>
    public string? OpenPath { get; set; }

    public Task<string?> OpenFileAsync(string title, string extension) => Task.FromResult(OpenPath);

    /// <summary>Texto que devuelve el diálogo de pedir texto (null = cancelar).</summary>
    public string? PromptAnswer { get; set; }

    public Task<string?> PromptAsync(string title, string message, string initial = "") => Task.FromResult(PromptAnswer);
}

/// <summary>Conecta con el agente en memoria sea cual sea la URL.</summary>
internal sealed class InMemoryConnector(AgentFactory agent) : IAgentConnector
{
    public (AgentService.AgentServiceClient Client, IDisposable Channel) Connect(PlcMonitor.Agent.Contracts.Client.AgentEndpoint endpoint)
    {
        var channel = GrpcChannel.ForAddress(agent.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = agent.Server.CreateHandler() });
        return (new AgentService.AgentServiceClient(channel), channel);
    }
}

internal static class Wait
{
    /// <summary>Espera (tiempo real, máximo 10 s) a que se cumpla la condición.</summary>
    public static async Task UntilAsync(Func<bool> condition, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No se cumplió: {what ?? "condición"}");
            await Task.Delay(20);
        }
    }
}
