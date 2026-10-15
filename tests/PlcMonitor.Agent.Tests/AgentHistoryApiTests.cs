using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Agent.Tests.AgentFactory;

namespace PlcMonitor.Agent.Tests;

/// <summary>Historiador a través de la API: configuración por tag, consulta y estado.</summary>
public class AgentHistoryApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TagConfiguration Historized(string address, string name) =>
        new() { Address = address, Name = name, History = new TagHistorySettings { Enabled = true } };

    [Fact]
    public async Task Historized_tags_can_be_queried_raw_and_aggregated()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [Historized("DB1.DBW0", "velocidad"), T("DB1.DBW2", "otro")]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 10;
        plc.Values["DB1.DBW2"] = 1;
        var client = agent.CreateGrpcClient();
        await EventuallyAsync(async () => await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct), t => t.Tags.All(x => x.Value.Quality == Quality.Good));
        plc.Values["DB1.DBW0"] = 30;
        await EventuallyAsync(async () => await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct), t => t.Tags.Single(x => x.Name == "velocidad").Value.Text == "30");

        var tags = await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct);
        tags.Tags.Single(t => t.Name == "velocidad").Historized.ShouldBeTrue();
        tags.Tags.Single(t => t.Name == "otro").Historized.ShouldBeFalse();

        var raw = await client.QueryHistoryAsync(new HistoryRequest { Device = "plc1", Tags = { "velocidad", "otro" } }, cancellationToken: Ct);
        var speed = raw.Series.Single(s => s.Tag == "velocidad");
        speed.Points.Where(p => p.Quality == Quality.Good).Select(p => p.Number).ShouldBe([10.0, 30.0]);
        raw.Series.Single(s => s.Tag == "otro").Points.ShouldBeEmpty();

        var avg = await client.QueryHistoryAsync(new HistoryRequest
        {
            Device = "plc1",
            Tags = { "velocidad" },
            From = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(-1)),
            To = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1)),
            IntervalMs = 2 * 3600 * 1000,
            Aggregate = HistoryAggregate.Max,
        }, cancellationToken: Ct);
        avg.Series.Single().Points.ShouldHaveSingleItem().Number.ShouldBe(30.0);

        var info = (await client.GetInfoAsync(new Empty(), cancellationToken: Ct)).Historian;
        info.Enabled.ShouldBeTrue();
        info.HistorizedTags.ShouldBe(1);
        info.RetentionDays.ShouldBe(90);
        info.Path.ShouldBe(Path.Combine(agent.Directory, "history"));
        info.Files.ShouldBe(1);
    }

    [Fact]
    public async Task History_settings_round_trip_through_the_device_configuration()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0", "t")]));
        var client = agent.CreateGrpcClient();
        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        device.ScanGroups[0].Tags[0].History.ShouldBeNull();

        device.ScanGroups[0].Tags[0].History = new HistoryConfig { Enabled = true, MinIntervalMs = 1000, Deadband = 5, DeadbandPercent = true, MaxIntervalMs = 60000 };
        await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true }, cancellationToken: Ct);

        var history = (await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct)).ScanGroups[0].Tags[0].History;
        (history.Enabled, history.MinIntervalMs, history.Deadband, history.DeadbandPercent, history.MaxIntervalMs).ShouldBe((true, 1000, 5.0, true, 60000));
        var saved = (await ConfigurationFile.LoadAsync(agent.ConfigPath, Ct)).Devices[0].ScanGroups[0].Tags[0].History!;
        saved.DeadbandMode.ShouldBe(DeadbandMode.Percent);

        device.ScanGroups[0].Tags[0].History.MaxIntervalMs = 10;
        var invalid = await Should.ThrowAsync<RpcException>(async () =>
            await client.PutDeviceAsync(new PutDeviceRequest { Device = device }, cancellationToken: Ct));
        invalid.Status.Detail.ShouldContain("maxIntervalMs");
    }

    [Fact]
    public async Task Disabled_historian_and_bad_requests_are_reported()
    {
        await using var agent = new AgentFactory();
        await File.WriteAllTextAsync(agent.ConfigPath, ConfigurationFile.Serialize(new AgentConfiguration { Historian = new HistorianSettings { Enabled = false } }), Ct);
        var client = agent.CreateGrpcClient();

        var disabled = await Should.ThrowAsync<RpcException>(async () =>
            await client.QueryHistoryAsync(new HistoryRequest { Device = "plc1", Tags = { "t" } }, cancellationToken: Ct));
        var missing = await Should.ThrowAsync<RpcException>(async () =>
            await client.QueryHistoryAsync(new HistoryRequest { Device = "plc1" }, cancellationToken: Ct));

        disabled.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
        missing.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        (await client.GetInfoAsync(new Empty(), cancellationToken: Ct)).Historian.Enabled.ShouldBeFalse();
    }

    private static TagConfiguration T(string address, string name) => new() { Address = address, Name = name };
}
