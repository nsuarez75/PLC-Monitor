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

    public FakeDriverFactory Driver { get; } = new();

    public string ConfigPath { get; } = Path.Combine(Path.GetTempPath(), $"plcmon-agent-{Guid.NewGuid():N}.json");

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
        File.Delete(ConfigPath);
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
