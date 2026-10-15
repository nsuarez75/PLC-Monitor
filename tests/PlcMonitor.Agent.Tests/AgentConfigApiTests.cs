using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Agent.Tests.AgentFactory;
using Tag = PlcMonitor.Engine.Configuration.TagConfiguration;

namespace PlcMonitor.Agent.Tests;

/// <summary>Gestión de la configuración y exploración a través de la API gRPC.</summary>
public class AgentConfigApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Tag T(string address, string? name = null, bool writable = false) => new() { Address = address, Name = name, Writable = writable };

    private static DeviceConfig NewDevice(string name, string host = "10.0.0.9", params TagConfig[] tags)
    {
        var device = new DeviceConfig
        {
            Name = name,
            Driver = "fake",
            Enabled = true,
            SettingsJson = $$"""{ "host": "{{host}}" }""",
            Reconnect = new ReconnectConfig { InitialDelayMs = 100, MaxDelayMs = 200, Multiplier = 2 },
        };
        var group = new ScanGroupConfig { Name = "rapido", IntervalMs = 50 };
        group.Tags.AddRange(tags);
        device.ScanGroups.Add(group);
        return device;
    }

    [Fact]
    public async Task GetInfo_exposes_driver_defaults_and_capabilities()
    {
        await using var agent = new AgentFactory();
        var driver = (await agent.CreateGrpcClient().GetInfoAsync(new Empty(), cancellationToken: Ct)).Drivers.ShouldHaveSingleItem();

        driver.DefaultSettingsJson.ShouldContain("\"host\"");
        driver.CanBrowse.ShouldBeTrue();
        driver.CanWrite.ShouldBeTrue();
    }

    [Fact]
    public async Task Configuration_round_trips_through_the_api()
    {
        await using var agent = new AgentFactory(Device("plc1", writesEnabled: true, tags: [T("DB1.DBW0", "velocidad", writable: true)]));
        var client = agent.CreateGrpcClient();

        var config = await client.GetConfigurationAsync(new Empty(), cancellationToken: Ct);
        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "PLC1" }, cancellationToken: Ct);

        config.Devices.ShouldHaveSingleItem().Name.ShouldBe("plc1");
        device.WritesEnabled.ShouldBeTrue();
        device.SettingsJson.ShouldContain("10.0.0.1");
        device.Reconnect.InitialDelayMs.ShouldBe(100);
        var tag = device.ScanGroups.ShouldHaveSingleItem().Tags.ShouldHaveSingleItem();
        (tag.Name, tag.Address, tag.Writable, tag.DataType).ShouldBe(("velocidad", "DB1.DBW0", true, DataType.Unknown));
    }

    [Fact]
    public async Task PutDevice_adds_a_device_persists_it_and_starts_polling()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        agent.Driver.Plc("nuevo").Values["DB5.DBW0"] = 77;
        var client = agent.CreateGrpcClient();

        var status = await client.PutDeviceAsync(
            new PutDeviceRequest { Device = NewDevice("nuevo", tags: new TagConfig { Name = "t", Address = "DB5.DBW0" }), Persist = true },
            cancellationToken: Ct);

        status.Name.ShouldBe("nuevo");
        status.TagCount.ShouldBe(1);
        var tags = await EventuallyAsync(
            async () => await client.ListTagsAsync(new ListTagsRequest { Device = "nuevo" }, cancellationToken: Ct),
            t => t.Tags.Single().Value.Quality == Quality.Good);
        tags.Tags[0].Value.IntValue.ShouldBe(77);
        var saved = await ConfigurationFile.LoadAsync(agent.ConfigPath, Ct);
        saved.Devices.Select(d => d.Name).ShouldBe(["plc1", "nuevo"]);
        saved.Devices[1].Settings.GetProperty("host").GetString().ShouldBe("10.0.0.9");
    }

    [Fact]
    public async Task PutDevice_without_persist_leaves_the_file_untouched()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        var client = agent.CreateGrpcClient();
        var before = await File.ReadAllTextAsync(agent.ConfigPath, Ct);

        await client.PutDeviceAsync(new PutDeviceRequest { Device = NewDevice("temporal") }, cancellationToken: Ct);

        (await File.ReadAllTextAsync(agent.ConfigPath, Ct)).ShouldBe(before);
        (await client.ListDevicesAsync(new Empty(), cancellationToken: Ct)).Devices.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Invalid_device_gives_all_errors_and_changes_nothing()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        var client = agent.CreateGrpcClient();
        var bad = NewDevice("plc/2", host: "", new TagConfig { Address = "" });
        bad.ScanGroups[0].IntervalMs = 1;

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await client.PutDeviceAsync(new PutDeviceRequest { Device = bad, Persist = true }, cancellationToken: Ct));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        ex.Status.Detail.ShouldContain("'/'");
        ex.Status.Detail.ShouldContain("Falta host");
        ex.Status.Detail.ShouldContain("intervalMs");
        ex.Status.Detail.ShouldContain("sin dirección");
        (await client.ListDevicesAsync(new Empty(), cancellationToken: Ct)).Devices.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Malformed_settings_json_is_invalid_argument()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateGrpcClient();
        var device = NewDevice("x");
        device.SettingsJson = "{ host: ";

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await client.PutDeviceAsync(new PutDeviceRequest { Device = device }, cancellationToken: Ct));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        ex.Status.Detail.ShouldContain("JSON");
    }

    [Fact]
    public async Task Rename_and_delete_are_announced_to_subscribers()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false), Device("plc2", enabled: false));
        var client = agent.CreateGrpcClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var call = client.Subscribe(new SubscribeRequest(), cancellationToken: cts.Token);
        (await call.ResponseStream.MoveNext(cts.Token)).ShouldBeTrue();

        var renamed = NewDevice("prensa");
        renamed.Enabled = false;
        await client.PutDeviceAsync(new PutDeviceRequest { Device = renamed, OriginalName = "plc1", Persist = true }, cancellationToken: Ct);
        await client.DeleteDeviceAsync(new DeleteDeviceRequest { Device = "plc2", Persist = true }, cancellationToken: Ct);

        var events = new List<SubscribeEvent>();
        while (!events.Any(e => e.EventCase == SubscribeEvent.EventOneofCase.DeviceRemoved && e.DeviceRemoved.Device == "plc2"))
        {
            (await call.ResponseStream.MoveNext(cts.Token)).ShouldBeTrue();
            events.Add(call.ResponseStream.Current);
        }
        events.Where(e => e.EventCase == SubscribeEvent.EventOneofCase.DeviceRemoved).Select(e => e.DeviceRemoved.Device).ShouldBe(["plc1", "plc2"]);
        events.Single(e => e.EventCase == SubscribeEvent.EventOneofCase.DeviceConfigured).DeviceConfigured.Device.Name.ShouldBe("prensa");
        (await ConfigurationFile.LoadAsync(agent.ConfigPath, Ct)).Devices.Select(d => d.Name).ShouldBe(["prensa"]);
    }

    [Fact]
    public async Task Unknown_device_config_operations_give_not_found()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateGrpcClient();

        async Task NotFound(Func<Task> call) =>
            (await Should.ThrowAsync<RpcException>(call)).StatusCode.ShouldBe(StatusCode.NotFound);

        await NotFound(async () => await client.GetDeviceConfigAsync(new DeviceRequest { Device = "x" }, cancellationToken: Ct));
        await NotFound(async () => await client.DeleteDeviceAsync(new DeleteDeviceRequest { Device = "x" }, cancellationToken: Ct));
        await NotFound(async () => await client.BrowseAsync(new BrowseRequest { Device = "x" }, cancellationToken: Ct));
        await NotFound(async () => await client.PutDeviceAsync(new PutDeviceRequest { Device = NewDevice("y"), OriginalName = "x" }, cancellationToken: Ct));
    }

    [Fact]
    public async Task Browse_filters_and_requires_a_connection()
    {
        await using var agent = new AgentFactory(Device("plc1"), Device("plc2", enabled: false));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.Motor"] = 1;
        plc.Values["DB1.Bomba"] = 2;
        var client = agent.CreateGrpcClient();
        await EventuallyAsync(async () => await client.ListDevicesAsync(new Empty(), cancellationToken: Ct), d => d.Devices[0].State == ConnectionState.Connected);

        var reply = await client.BrowseAsync(new BrowseRequest { Device = "plc1", Filter = "motor" }, cancellationToken: Ct);
        var offline = await Should.ThrowAsync<RpcException>(async () =>
            await client.BrowseAsync(new BrowseRequest { Device = "plc2" }, cancellationToken: Ct));

        reply.TotalSymbols.ShouldBe(2);
        var item = reply.Items.ShouldHaveSingleItem();
        (item.Address, item.DataType, item.NativeType).ShouldBe(("DB1.Motor", DataType.Int32, "Fake"));
        offline.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
    }
}
