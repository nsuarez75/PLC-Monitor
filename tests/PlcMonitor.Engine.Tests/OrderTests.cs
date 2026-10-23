using PlcMonitor.Abstractions;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Engine.Tests.EngineHarness;

namespace PlcMonitor.Engine.Tests;

/// <summary>Orden de los dispositivos y orden de visualización de los tags.</summary>
public class OrderTests
{
    private static async Task<List<EngineEvent>> DrainAsync(EngineSubscription subscription, Func<List<EngineEvent>, bool> until)
    {
        var events = new List<EngineEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!until(events))
            events.Add(await subscription.Events.ReadAsync(cts.Token));
        return events;
    }

    private static DeviceConfiguration TwoGroups(params string[] order)
    {
        var device = Device("plc1", tags: [Tag("DB1.DBW0", "a"), Tag("DB1.DBW2", "b")]);
        device.ScanGroups.Add(new ScanGroupConfiguration { Name = "lento", IntervalMs = 1000, Tags = [Tag("DB1.DBW4", "c")] });
        device.TagOrder = order.Length == 0 ? null : [.. order];
        return device;
    }

    [Fact]
    public void Tags_follow_the_tag_order_and_the_rest_keep_the_group_order()
    {
        DeviceSupervisor.BuildTags(TwoGroups()).Select(t => t.Key.Tag).ShouldBe(["a", "b", "c"]);
        DeviceSupervisor.BuildTags(TwoGroups("c", "a")).Select(t => t.Key.Tag).ShouldBe(["c", "a", "b"]);
        // Los nombres que ya no existen (o repetidos) no molestan.
        DeviceSupervisor.BuildTags(TwoGroups("x", "b", "b")).Select(t => t.Key.Tag).ShouldBe(["b", "a", "c"]);
    }

    [Fact]
    public async Task Changing_the_tag_order_does_not_reconnect_and_keeps_the_values()
    {
        await using var h = new EngineHarness(TwoGroups());
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        plc.Values["DB1.DBW2"] = 2;
        plc.Values["DB1.DBW4"] = 3;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Engine.GetTags("plc1").All(t => t.Value.Quality == QualityCode.Good));
        using var subscription = h.Engine.Subscribe();

        await h.Engine.PutDeviceAsync(TwoGroups("c", "b", "a"));

        var configured = (await DrainAsync(subscription, e => e.OfType<DeviceConfigured>().Any())).OfType<DeviceConfigured>().Single();
        configured.Tags.Select(t => t.Definition.Key.Tag).ShouldBe(["c", "b", "a"]);
        configured.Tags.ShouldAllBe(t => t.Value.Quality == QualityCode.Good);
        h.Engine.GetTags("plc1").Select(t => t.Definition.Key.Tag).ShouldBe(["c", "b", "a"]);
        h.Engine.SnapshotConfiguration().Devices[0].TagOrder.ShouldBe(["c", "b", "a"]);
        plc.ConnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task Devices_are_reordered_and_announced_without_touching_the_connections()
    {
        await using var h = new EngineHarness(Device("a"), Device("b"), Device("c"));
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Engine.GetDevices().All(d => d.State == ConnectionState.Connected));
        using var subscription = h.Engine.Subscribe();

        var order = await h.Engine.ReorderDevicesAsync(["C", "a", "nada", "c"]);

        order.ShouldBe(["c", "a", "b"]);
        h.Engine.GetDevices().Select(d => d.Device).ShouldBe(["c", "a", "b"]);
        h.Engine.SnapshotConfiguration().Devices.Select(d => d.Name).ShouldBe(["c", "a", "b"]);
        var events = await DrainAsync(subscription, e => e.OfType<DevicesReordered>().Any());
        events.OfType<DevicesReordered>().Single().Devices.ShouldBe(["c", "a", "b"]);
        h.Driver.Plc("a").ConnectCount.ShouldBe(1);
        using (var again = h.Engine.Subscribe())
            again.InitialDevices.Select(d => d.Device).ShouldBe(["c", "a", "b"]);
    }

    [Fact]
    public async Task Same_order_is_not_announced()
    {
        await using var h = new EngineHarness(Device("a", enabled: false), Device("b", enabled: false));
        await h.Engine.StartAsync();
        using var subscription = h.Engine.Subscribe();

        (await h.Engine.ReorderDevicesAsync(["a"])).ShouldBe(["a", "b"]);

        subscription.Events.TryRead(out _).ShouldBeFalse();
    }
}
