using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentModbus;
using Google.Protobuf.WellKnownTypes;
using DriverJson = PlcMonitor.Abstractions.DriverJson;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Drivers.Modbus;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Agent.Tests.AgentFactory;
using Tag = PlcMonitor.Engine.Configuration.TagConfiguration;

namespace PlcMonitor.Agent.Tests;

/// <summary>El agente con el driver Modbus real contra un esclavo Modbus TCP simulado en el propio proceso.</summary>
public sealed class AgentModbusTests : IDisposable
{
    private readonly ModbusTcpServer _slave = new();
    private readonly int _port;

    public AgentModbusTests()
    {
        _slave.AddUnit(1);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        _slave.Start(new IPEndPoint(IPAddress.Loopback, _port));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _slave.Dispose();

    private AgentFactory Agent(bool writesEnabled, params Tag[] tags)
    {
        var device = new DeviceConfiguration
        {
            Name = "variador",
            Driver = ModbusDriverFactory.Id,
            WritesEnabled = writesEnabled,
            Settings = JsonSerializer.SerializeToElement(new ModbusSettings { Host = "127.0.0.1", Port = _port, UnitId = 1 }, DriverJson.Options),
            Reconnect = new ReconnectSettings { InitialDelayMs = 100, MaxDelayMs = 200 },
            ScanGroups = { new ScanGroupConfiguration { Name = "g", IntervalMs = 50, Tags = [.. tags] } },
        };
        var agent = new AgentFactory(device);
        agent.ExtraDrivers.Add(new ModbusDriverFactory());
        return agent;
    }

    [Fact]
    public async Task The_agent_offers_modbus_without_browsing()
    {
        await using var agent = Agent(false);
        var info = await agent.CreateGrpcClient().GetInfoAsync(new Empty(), cancellationToken: Ct);
        var modbus = info.Drivers.Single(d => d.Id == "modbus");
        modbus.CanBrowse.ShouldBeFalse();
        modbus.CanWrite.ShouldBeTrue();
        modbus.DefaultSettingsJson.ShouldContain("\"port\": 502");
    }

    [Fact]
    public async Task Scaled_tags_are_read_and_written_through_both_keys()
    {
        _slave.GetHoldingRegisters(1).SetBigEndian<short>(0, 13824);
        _slave.GetHoldingRegisters(1).SetBigEndian(2, 21.5f);
        await using var agent = Agent(true,
            new Tag { Name = "presion", Address = "HR0", Writable = true, Scaling = new TagScaling { RawMax = 27648, EngMax = 10 } },
            new Tag { Name = "temperatura", Address = "40003:FLOAT" });
        var client = agent.CreateGrpcClient();

        var tags = await EventuallyAsync(async () => await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct),
            t => t.Tags.Count == 2 && t.Tags.All(x => x.Value.Quality == Quality.Good));
        tags.Tags.Single(t => t.Name == "presion").Value.Text.ShouldBe("5");
        tags.Tags.Single(t => t.Name == "temperatura").Value.Text.ShouldBe("21.5");

        var denied = await client.WriteAsync(new WriteRequest { Device = "variador", Tag = "temperatura", Value = "1", User = "nacho" }, cancellationToken: Ct);
        var written = await client.WriteAsync(new WriteRequest { Device = "variador", Tag = "presion", Value = "2.5", User = "nacho" }, cancellationToken: Ct);

        denied.Quality.ShouldBe(Quality.BadWriteDisabled);
        written.Success.ShouldBeTrue(written.Error);
        _slave.GetHoldingRegisters(1).GetBigEndian<short>(0).ShouldBe((short)6912);
        agent.AuditLog.ShouldContain(m => m.Contains("presion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Writes_are_refused_while_the_device_key_is_off()
    {
        await using var agent = Agent(false, new Tag { Name = "consigna", Address = "HR5", Writable = true });
        var client = agent.CreateGrpcClient();
        await EventuallyAsync(async () => await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct),
            t => t.Tags.Count == 1 && t.Tags[0].Value.Quality == Quality.Good);

        var reply = await client.WriteAsync(new WriteRequest { Device = "variador", Tag = "consigna", Value = "9", User = "nacho" }, cancellationToken: Ct);

        reply.Quality.ShouldBe(Quality.BadWriteDisabled);
        _slave.GetHoldingRegisters(1).GetBigEndian<short>(5).ShouldBe((short)0);
    }
}
