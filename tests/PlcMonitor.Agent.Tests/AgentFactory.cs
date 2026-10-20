using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Core;
using PlcMonitor.Engine.Configuration;
using PlcMonitor.Engine.Tests;

namespace PlcMonitor.Agent.Tests;

/// <summary>Agente en memoria con driver simulado y su configuración en un fichero temporal.</summary>
internal sealed class AgentFactory : WebApplicationFactory<Program>
{
    private readonly List<GrpcChannel> _channels = [];

    public AgentFactory(params DeviceConfiguration[] devices)
    {
        if (devices.Length > 0)
            File.WriteAllText(ConfigPath, ConfigurationFile.Serialize(new AgentConfiguration { Devices = [.. devices] }));
    }

    /// <summary>Agente con una configuración completa (dispositivos, transformaciones...).</summary>
    public AgentFactory(AgentConfiguration configuration) =>
        File.WriteAllText(ConfigPath, ConfigurationFile.Serialize(configuration));

    public FakeDriverFactory Driver { get; } = new();

    /// <summary>Carpeta propia del agente de prueba (configuración e histórico).</summary>
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("plcmon-agent-").FullName;

    public string ConfigPath => Path.Combine(Directory, "agent.json");

    /// <summary>Ajustes adicionales del agente (p.ej. Agent:Security:TokenSha256). Se aplican al arrancar.</summary>
    public Dictionary<string, string> Settings { get; } = [];

    public static DeviceConfiguration Device(string name, bool enabled = true, bool writesEnabled = false, params TagConfiguration[] tags) => new()
    {
        Name = name,
        Driver = FakeDriverFactory.Id,
        Enabled = enabled,
        WritesEnabled = writesEnabled,
        Settings = FakeDriverFactory.Settings(),
        Reconnect = new ReconnectSettings { InitialDelayMs = 100, MaxDelayMs = 200 },
        ScanGroups = { new ScanGroupConfiguration { Name = "g", IntervalMs = 50, Tags = [.. tags] } },
    };

    public AgentService.AgentServiceClient CreateClient(out GrpcChannel channel)
    {
        channel = GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });
        _channels.Add(channel);
        return new AgentService.AgentServiceClient(channel);
    }

    public AgentService.AgentServiceClient CreateGrpcClient() => CreateClient(out _);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Agent:ConfigPath", ConfigPath);
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DriverRegistry>();
            services.AddSingleton(new DriverRegistry([Driver]));
        });
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var channel in _channels)
            channel.Dispose();
        base.Dispose(disposing);
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // El historiador puede tardar un instante en soltar sus ficheros: es una carpeta temporal.
        }
    }

    /// <summary>Repite la llamada hasta que el resultado cumple la condición (máximo 10 s).</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> call, Func<T, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var result = await call();
            if (condition(result))
                return result;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No se cumplió la condición. Último resultado: {result}");
            await Task.Delay(20);
        }
    }
}
