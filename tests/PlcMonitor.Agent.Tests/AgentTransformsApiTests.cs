using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Agent.Tests.AgentFactory;
using Tag = PlcMonitor.Engine.Configuration.TagConfiguration;

namespace PlcMonitor.Agent.Tests;

/// <summary>Transformaciones de valores y grupos de visualización a través de la API gRPC.</summary>
public class AgentTransformsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PutTransformsRequest Estados(bool persist = true)
    {
        var transform = new Agent.Contracts.ValueTransform { Name = "Estados" };
        transform.Entries.Add(new Agent.Contracts.TransformEntry { Value = 0, Text = "Parado" });
        transform.Entries.Add(new Agent.Contracts.TransformEntry { Value = 1, Text = "Marcha" });
        var request = new PutTransformsRequest { Persist = persist };
        request.Transforms.Add(transform);
        return request;
    }

    [Fact]
    public async Task Transforms_are_saved_listed_and_streamed()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false, tags: [new Tag { Address = "DB1.DBW0", Name = "motor" }]));
        var client = agent.CreateGrpcClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var call = client.Subscribe(new SubscribeRequest(), cancellationToken: cts.Token);
        (await call.ResponseStream.MoveNext(cts.Token)).ShouldBeTrue();
        call.ResponseStream.Current.Snapshot.Transforms.ShouldBeEmpty();

        var saved = await client.PutTransformsAsync(Estados(), cancellationToken: Ct);

        saved.Transforms.ShouldHaveSingleItem().Entries.Select(e => e.Text).ShouldBe(["Parado", "Marcha"]);
        (await client.GetTransformsAsync(new Empty(), cancellationToken: Ct)).Transforms.ShouldHaveSingleItem().Name.ShouldBe("Estados");
        SubscribeEvent next;
        do
        {
            (await call.ResponseStream.MoveNext(cts.Token)).ShouldBeTrue();
            next = call.ResponseStream.Current;
        }
        while (next.EventCase != SubscribeEvent.EventOneofCase.Transforms);
        next.Transforms.Transforms.ShouldHaveSingleItem().Name.ShouldBe("Estados");
        var file = await ConfigurationFile.LoadAsync(agent.ConfigPath, Ct);
        file.Transforms.ShouldHaveSingleItem().Entries.Select(e => e.Value).ShouldBe([0L, 1L]);
    }

    [Fact]
    public async Task Transform_and_folder_of_a_tag_round_trip_and_reach_the_tag_list()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false, tags: [new Tag { Address = "DB1.DBW0", Name = "motor" }]));
        var client = agent.CreateGrpcClient();
        await client.PutTransformsAsync(Estados(), cancellationToken: Ct);

        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        var tag = device.ScanGroups[0].Tags[0];
        tag.Transform = "Estados";
        tag.Folder = " Cinta 1 ";
        await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true }, cancellationToken: Ct);

        var config = (await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct)).ScanGroups[0].Tags[0];
        (config.Transform, config.Folder).ShouldBe(("Estados", "Cinta 1"));
        var listed = (await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct)).Tags.ShouldHaveSingleItem();
        (listed.Transform, listed.Folder).ShouldBe(("Estados", "Cinta 1"));
        var file = (await ConfigurationFile.LoadAsync(agent.ConfigPath, Ct)).Devices[0].ScanGroups[0].Tags[0];
        (file.Transform, file.Folder).ShouldBe(("Estados", "Cinta 1"));
    }

    [Fact]
    public async Task Unit_and_decimals_of_a_tag_round_trip_and_reach_the_tag_list()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false, tags: [new Tag { Address = "DB1.DBD0", Name = "presion" }, new Tag { Address = "DB1.DBW4", Name = "contador" }]));
        var client = agent.CreateGrpcClient();

        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        device.ScanGroups[0].Tags[0].HasDecimals.ShouldBeFalse();
        device.ScanGroups[0].Tags[0].Unit = " bar ";
        device.ScanGroups[0].Tags[0].Decimals = 0;
        await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true }, cancellationToken: Ct);

        var config = (await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct)).ScanGroups[0].Tags;
        (config[0].Unit, config[0].HasDecimals, config[0].Decimals).ShouldBe(("bar", true, 0));
        (config[1].Unit, config[1].HasDecimals).ShouldBe(("", false));
        var listed = (await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct)).Tags.Single(t => t.Name == "presion");
        (listed.Unit, listed.HasDecimals, listed.Decimals).ShouldBe(("bar", true, 0));
        var file = (await ConfigurationFile.LoadAsync(agent.ConfigPath, Ct)).Devices[0].ScanGroups[0].Tags;
        (file[0].Unit, file[0].Decimals).ShouldBe(("bar", 0));
        file[1].Decimals.ShouldBeNull();
    }

    [Fact]
    public async Task Scaling_of_a_tag_round_trips_and_values_arrive_scaled_with_the_raw_text()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [new Tag { Address = "DB1.DBW0", Name = "presion" }, new Tag { Address = "DB1.DBW4", Name = "contador" }]));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 13824;
        agent.Driver.Plc("plc1").Values["DB1.DBW4"] = 7;
        var client = agent.CreateGrpcClient();

        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        device.ScanGroups[0].Tags[0].Scaling.ShouldBeNull();
        device.ScanGroups[0].Tags[0].Scaling = new Scaling { RawMin = 0, RawMax = 27648, EngMin = 0, EngMax = 10, Clamp = true };
        await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true }, cancellationToken: Ct);

        var config = (await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct)).ScanGroups[0].Tags;
        config[0].Scaling.ShouldBe(new Scaling { RawMin = 0, RawMax = 27648, EngMin = 0, EngMax = 10, Clamp = true });
        config[1].Scaling.ShouldBeNull();
        var tags = await EventuallyAsync(
            async () => await client.ListTagsAsync(new ListTagsRequest(), cancellationToken: Ct),
            t => t.Tags.Single(x => x.Name == "presion").Value.Text == "5");
        var scaled = tags.Tags.Single(t => t.Name == "presion");
        scaled.Scaling.EngMax.ShouldBe(10);
        (scaled.Value.DoubleValue, scaled.Value.DataType, scaled.Value.RawText).ShouldBe((5.0, DataType.Double, "13824"));
        var plain = tags.Tags.Single(t => t.Name == "contador");
        (plain.Scaling, plain.Value.RawText).ShouldBe((null, ""));
        var file = (await ConfigurationFile.LoadAsync(agent.ConfigPath, Ct)).Devices[0].ScanGroups[0].Tags;
        file[0].Scaling.ShouldBe(new TagScaling { RawMin = 0, RawMax = 27648, EngMin = 0, EngMax = 10, Clamp = true });
        file[1].Scaling.ShouldBeNull();
    }

    [Fact]
    public async Task Invalid_scaling_gives_invalid_argument()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false, tags: [new Tag { Address = "DB1.DBW0", Name = "presion" }]));
        var client = agent.CreateGrpcClient();
        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        device.ScanGroups[0].Tags[0].Scaling = new Scaling { RawMin = 100, RawMax = 100, EngMin = 0, EngMax = 10 };

        var ex = await Should.ThrowAsync<RpcException>(() => client.PutDeviceAsync(new PutDeviceRequest { Device = device }, cancellationToken: Ct).ResponseAsync);

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        ex.Status.Detail.ShouldContain("el bruto mínimo y el máximo no pueden ser iguales");
    }

    [Fact]
    public async Task Invalid_decimals_give_invalid_argument()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false, tags: [new Tag { Address = "DB1.DBD0", Name = "presion" }]));
        var client = agent.CreateGrpcClient();
        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        device.ScanGroups[0].Tags[0].Decimals = 42;

        var ex = await Should.ThrowAsync<RpcException>(() => client.PutDeviceAsync(new PutDeviceRequest { Device = device }, cancellationToken: Ct).ResponseAsync);

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        ex.Status.Detail.ShouldContain("los decimales deben estar entre 0");
    }

    [Fact]
    public async Task Invalid_transforms_give_invalid_argument()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false, tags: [new Tag { Address = "DB1.DBW0", Name = "motor" }]));
        var client = agent.CreateGrpcClient();
        await client.PutTransformsAsync(Estados(), cancellationToken: Ct);
        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        device.ScanGroups[0].Tags[0].Transform = "Estados";
        await client.PutDeviceAsync(new PutDeviceRequest { Device = device }, cancellationToken: Ct);

        var inUse = await Should.ThrowAsync<RpcException>(() => client.PutTransformsAsync(new PutTransformsRequest(), cancellationToken: Ct).ResponseAsync);
        var unknown = device.Clone();
        unknown.ScanGroups[0].Tags[0].Transform = "Otra";
        var badTag = await Should.ThrowAsync<RpcException>(() => client.PutDeviceAsync(new PutDeviceRequest { Device = unknown }, cancellationToken: Ct).ResponseAsync);

        inUse.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        inUse.Status.Detail.ShouldContain("plc1/motor");
        badTag.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        badTag.Status.Detail.ShouldContain("'Otra' no existe");
    }
}
