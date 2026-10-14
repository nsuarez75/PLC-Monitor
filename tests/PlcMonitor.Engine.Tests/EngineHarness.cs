using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Engine.Tests;

/// <summary>Motor con driver simulado y reloj controlado.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public EngineHarness(params DeviceConfiguration[] devices)
    {
        Configuration = new AgentConfiguration { Devices = [.. devices] };
        Engine = new TagEngine(Configuration, new DriverRegistry([Driver]), NullLoggerFactory.Instance, Time);
    }

    public FakeDriverFactory Driver { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

    public AgentConfiguration Configuration { get; }

    public TagEngine Engine { get; }

    public static DeviceConfiguration Device(string name, bool enabled = true, bool writesEnabled = false, int intervalMs = 100, params TagConfiguration[] tags) => new()
    {
        Name = name,
        Driver = FakeDriverFactory.Id,
        Enabled = enabled,
        WritesEnabled = writesEnabled,
        Settings = FakeDriverFactory.Settings(),
        Reconnect = new ReconnectSettings { InitialDelayMs = 1000, MaxDelayMs = 8000, Multiplier = 2 },
        ScanGroups = { new ScanGroupConfiguration { Name = "g", IntervalMs = intervalMs, Tags = [.. tags] } },
    };

    public static TagConfiguration Tag(string address, string? name = null, bool writable = false, TagDataType? dataType = null) =>
        new() { Address = address, Name = name, Writable = writable, DataType = dataType };

    public TagValue Value(string device, string tag) => Engine.GetTags(device).Single(t => t.Definition.Key.Tag == tag).Value;

    /// <summary>Avanza el reloj simulado a pasos hasta que se cumple la condición (máximo 10 s reales).</summary>
    public async Task AdvanceUntilAsync(Func<bool> condition, int stepMs = 50)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("La condición no se cumplió a tiempo.");
            Time.Advance(TimeSpan.FromMilliseconds(stepMs));
            await Task.Delay(2);
        }
    }

    /// <summary>Espera en tiempo real (sin mover el reloj simulado).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("La condición no se cumplió a tiempo.");
            await Task.Delay(2);
        }
    }

    public async ValueTask DisposeAsync() => await Engine.DisposeAsync();
}
