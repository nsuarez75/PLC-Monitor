using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Agent.Tests.AgentFactory;
using Tag = PlcMonitor.Engine.Configuration.TagConfiguration;

namespace PlcMonitor.Agent.Tests;

public class AgentApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Tag T(string address, string? name = null, bool writable = false) => new() { Address = address, Name = name, Writable = writable };

    [Fact]
    public async Task GetInfo_reports_drivers_and_config_path()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateGrpcClient();

        var info = await client.GetInfoAsync(new Empty(), cancellationToken: Ct);

        info.Drivers.ShouldHaveSingleItem().Id.ShouldBe("fake");
        info.ConfigPath.ShouldBe(agent.ConfigPath);
        info.Machine.ShouldBe(Environment.MachineName);
    }

    [Fact]
    public async Task Agent_without_config_file_starts_empty()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateGrpcClient();

        (await client.ListDevicesAsync(new Empty(), cancellationToken: Ct)).Devices.ShouldBeEmpty();
    }

    [Fact]
    public async Task Devices_and_tags_are_listed_with_live_values()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0", "velocidad")]), Device("plc2", enabled: false));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 1500;
        var client = agent.CreateGrpcClient();

        var tags = await EventuallyAsync(
            async () => await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct),
            t => t.Tags.All(x => x.Value.Quality == Quality.Good));
        var devices = await client.ListDevicesAsync(new Empty(), cancellationToken: Ct);

        var tag = tags.Tags.ShouldHaveSingleItem();
        tag.Device.ShouldBe("plc1");
        tag.Name.ShouldBe("velocidad");
        tag.Address.ShouldBe("DB1.DBW0");
        tag.IntervalMs.ShouldBe(50);
        tag.Value.Text.ShouldBe("1500");
        tag.Value.IntValue.ShouldBe(1500);
        tag.Value.DataType.ShouldBe(DataType.Int32);

        devices.Devices.Select(d => (d.Name, d.Enabled, d.State, d.TagCount)).ShouldBe(
            [("plc1", true, ConnectionState.Connected, 1), ("plc2", false, ConnectionState.Disconnected, 0)]);
    }

    [Fact]
    public async Task Subscribe_sends_a_snapshot_and_then_changes()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0")]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        var client = agent.CreateGrpcClient();
        await EventuallyAsync(async () => await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct), t => t.Tags[0].Value.Quality == Quality.Good);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var call = client.Subscribe(new SubscribeRequest(), cancellationToken: cts.Token);
        var stream = call.ResponseStream;

        (await stream.MoveNext(cts.Token)).ShouldBeTrue();
        var snapshot = stream.Current.Snapshot.ShouldNotBeNull();
        snapshot.Devices.ShouldHaveSingleItem().State.ShouldBe(ConnectionState.Connected);
        snapshot.Tags.ShouldHaveSingleItem().Value.Text.ShouldBe("1");

        plc.Values["DB1.DBW0"] = 2;
        (await stream.MoveNext(cts.Token)).ShouldBeTrue();
        var update = stream.Current.TagUpdates.Updates.ShouldHaveSingleItem();
        update.Device.ShouldBe("plc1");
        update.Tag.ShouldBe("DB1.DBW0");
        update.Value.Text.ShouldBe("2");
    }

    [Fact]
    public async Task Subscribe_streams_device_status_changes()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0")]));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        var client = agent.CreateGrpcClient();
        await EventuallyAsync(async () => await client.ListDevicesAsync(new Empty(), cancellationToken: Ct), d => d.Devices[0].State == ConnectionState.Connected);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var call = client.Subscribe(new SubscribeRequest(), cancellationToken: cts.Token);
        (await call.ResponseStream.MoveNext(cts.Token)).ShouldBeTrue();

        agent.Driver.Plc("plc1").Unplugged = true;
        Device? faulted = null;
        while (faulted is null && await call.ResponseStream.MoveNext(cts.Token))
        {
            if (call.ResponseStream.Current.DeviceStatus is { State: ConnectionState.Faulted } status)
                faulted = status;
        }

        faulted.ShouldNotBeNull();
        faulted.Detail.ShouldBe("Cable desconectado");
        faulted.NextRetry.ShouldNotBeNull();
    }

    [Fact]
    public async Task Subscribe_can_be_limited_to_some_devices()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0")]), Device("plc2", tags: [T("DB1.DBW0")]));
        var client = agent.CreateGrpcClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        using var call = client.Subscribe(new SubscribeRequest { Devices = { "PLC2" } }, cancellationToken: cts.Token);
        (await call.ResponseStream.MoveNext(cts.Token)).ShouldBeTrue();

        var snapshot = call.ResponseStream.Current.Snapshot;
        snapshot.Devices.ShouldHaveSingleItem().Name.ShouldBe("plc2");
        snapshot.Tags.ShouldHaveSingleItem().Device.ShouldBe("plc2");
    }

    [Fact]
    public async Task Unknown_device_gives_not_found()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        var client = agent.CreateGrpcClient();

        async Task NotFound(Func<Task> call) =>
            (await Should.ThrowAsync<RpcException>(call)).StatusCode.ShouldBe(StatusCode.NotFound);

        await NotFound(async () => await client.ListTagsAsync(new ListTagsRequest { Device = "x" }, cancellationToken: Ct));
        await NotFound(async () => await client.ReadAsync(new ReadRequest { Device = "x", Addresses = { "MW0" } }, cancellationToken: Ct));
        await NotFound(async () => await client.WriteAsync(new WriteRequest { Device = "x", Tag = "t", Value = "1" }, cancellationToken: Ct));
        await NotFound(async () => await client.SetDeviceEnabledAsync(new SetDeviceEnabledRequest { Device = "x" }, cancellationToken: Ct));
        await NotFound(async () => await client.GetDeviceInfoAsync(new DeviceRequest { Device = "x" }, cancellationToken: Ct));
        await NotFound(async () =>
        {
            using var call = client.Subscribe(new SubscribeRequest { Devices = { "x" } }, cancellationToken: Ct);
            await call.ResponseStream.MoveNext(Ct);
        });
    }

    [Fact]
    public async Task Device_can_be_disabled_and_the_change_persisted()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0")]));
        var client = agent.CreateGrpcClient();

        var device = await client.SetDeviceEnabledAsync(new SetDeviceEnabledRequest { Device = "plc1", Enabled = false, Persist = true }, cancellationToken: Ct);

        device.Enabled.ShouldBeFalse();
        device.State.ShouldBe(ConnectionState.Disconnected);
        var saved = await ConfigurationFile.LoadAsync(agent.ConfigPath, Ct);
        saved.Devices.ShouldHaveSingleItem().Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Enabling_without_persist_leaves_the_file_untouched()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false));
        var client = agent.CreateGrpcClient();
        var before = await File.ReadAllTextAsync(agent.ConfigPath, Ct);

        var device = await client.SetDeviceEnabledAsync(new SetDeviceEnabledRequest { Device = "plc1", Enabled = true }, cancellationToken: Ct);

        device.Enabled.ShouldBeTrue();
        (await File.ReadAllTextAsync(agent.ConfigPath, Ct)).ShouldBe(before);
    }

    [Fact]
    public async Task Write_goes_through_the_double_key()
    {
        await using var agent = new AgentFactory(Device("plc1", writesEnabled: true, tags: [T("DB1.DBW0", "ro"), T("DB1.DBW2", "rw", writable: true)]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 0;
        plc.Values["DB1.DBW2"] = 0;
        var client = agent.CreateGrpcClient();
        await EventuallyAsync(async () => await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct), t => t.Tags.All(x => x.Value.Quality == Quality.Good));

        var denied = await client.WriteAsync(new WriteRequest { Device = "plc1", Tag = "ro", Value = "7", User = "nacho" }, cancellationToken: Ct);
        var written = await client.WriteAsync(new WriteRequest { Device = "plc1", Tag = "rw", Value = "7", User = "nacho" }, cancellationToken: Ct);

        denied.Success.ShouldBeFalse();
        denied.Quality.ShouldBe(Quality.BadWriteDisabled);
        written.Success.ShouldBeTrue();
        plc.Writes.ShouldHaveSingleItem().Value.ShouldBe(7);
        await EventuallyAsync(async () => await client.ListTagsAsync(new ListTagsRequest { Device = "plc1" }, cancellationToken: Ct),
            t => t.Tags.Single(x => x.Name == "rw").Value.Text == "7");
    }

    [Fact]
    public async Task Read_returns_on_demand_values()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        agent.Driver.Plc("plc1").Values["MW10"] = 12;
        var client = agent.CreateGrpcClient();
        await EventuallyAsync(async () => await client.ListDevicesAsync(new Empty(), cancellationToken: Ct), d => d.Devices[0].State == ConnectionState.Connected);

        var reply = await client.ReadAsync(new ReadRequest { Device = "plc1", Addresses = { "MW10", "MW99" } }, cancellationToken: Ct);

        reply.Values[0].IntValue.ShouldBe(12);
        reply.Values[1].Quality.ShouldBe(Quality.BadAddressInvalid);
        reply.Values[1].Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task Read_without_addresses_is_invalid()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        var client = agent.CreateGrpcClient();

        var ex = await Should.ThrowAsync<RpcException>(async () => await client.ReadAsync(new ReadRequest { Device = "plc1" }, cancellationToken: Ct));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }
}
