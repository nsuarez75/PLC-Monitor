using PlcMonitor.Abstractions;
using static PlcMonitor.Engine.Tests.EngineHarness;

namespace PlcMonitor.Engine.Tests;

/// <summary>Cambios de configuración en caliente: alta, modificación, renombrado y baja de dispositivos; exploración.</summary>
public class TagEngineConfigurationTests
{
    private static async Task<List<EngineEvent>> DrainAsync(EngineSubscription subscription, Func<List<EngineEvent>, bool> until)
    {
        var events = new List<EngineEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!until(events))
            events.Add(await subscription.Events.ReadAsync(cts.Token));
        return events;
    }

    [Fact]
    public async Task New_device_is_added_started_and_announced()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "a")]));
        h.Driver.Plc("plc2").Values["DB2.DBW0"] = 5;
        await h.Engine.StartAsync();
        using var subscription = h.Engine.Subscribe();

        var status = await h.Engine.PutDeviceAsync(Device("plc2", tags: [Tag("DB2.DBW0", "b")]));

        status.Device.ShouldBe("plc2");
        h.Engine.GetDevices().Select(d => d.Device).ShouldBe(["plc1", "plc2"]);
        await WaitUntilAsync(() => h.Value("plc2", "b").Quality == QualityCode.Good);
        h.Value("plc2", "b").Value.ShouldBe(5);
        var events = await DrainAsync(subscription, e => e.OfType<DeviceConfigured>().Any());
        var configured = events.OfType<DeviceConfigured>().Single();
        configured.Status.Device.ShouldBe("plc2");
        configured.Tags.ShouldHaveSingleItem().Definition.Key.Tag.ShouldBe("b");
    }

    [Fact]
    public async Task Existing_device_is_restarted_with_its_new_tags()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "viejo")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        plc.Values["DB1.DBW2"] = 2;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "viejo").Quality == QualityCode.Good);

        await h.Engine.PutDeviceAsync(Device("plc1", writesEnabled: true, tags: [Tag("DB1.DBW2", "nuevo", writable: true)]));

        await WaitUntilAsync(() => h.Engine.GetTags("plc1").Single().Value.Quality == QualityCode.Good);
        h.Engine.GetTags("plc1").Select(t => t.Definition.Key.Tag).ShouldBe(["nuevo"]);
        h.Value("plc1", "nuevo").Value.ShouldBe(2);
        h.Engine.GetDevice("plc1").WritesEnabled.ShouldBeTrue();
        h.Engine.SnapshotConfiguration().Devices.ShouldHaveSingleItem().ScanGroups[0].Tags.ShouldHaveSingleItem().Name.ShouldBe("nuevo");
        plc.ConnectCount.ShouldBe(2);
    }

    [Fact]
    public async Task Renaming_removes_the_old_device()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false, tags: [Tag("DB1.DBW0")]), Device("plc2", enabled: false));
        await h.Engine.StartAsync();
        using var subscription = h.Engine.Subscribe();

        await h.Engine.PutDeviceAsync(Device("prensa", enabled: false, tags: [Tag("DB1.DBW0")]), originalName: "plc1");

        h.Engine.GetDevices().Select(d => d.Device).ShouldBe(["prensa", "plc2"]);
        Should.Throw<KeyNotFoundException>(() => h.Engine.GetDevice("plc1"));
        var events = await DrainAsync(subscription, e => e.OfType<DeviceConfigured>().Any());
        events.OfType<DeviceRemoved>().ShouldHaveSingleItem().Device.ShouldBe("plc1");
    }

    [Fact]
    public async Task Invalid_change_is_rejected_and_nothing_changes()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false, tags: [Tag("DB1.DBW0", "a")]), Device("plc2", enabled: false));
        await h.Engine.StartAsync();

        var duplicateTag = await Should.ThrowAsync<EngineConfigurationException>(
            () => h.Engine.PutDeviceAsync(Device("plc1", tags: [Tag("DB1.DBW0", "x"), Tag("DB1.DBW2", "x")])));
        var nameTaken = await Should.ThrowAsync<EngineConfigurationException>(
            () => h.Engine.PutDeviceAsync(Device("plc2", enabled: false), originalName: "plc1"));
        var withoutHost = Device("plc3");
        withoutHost.Settings = FakeDriverFactory.Settings("");
        var badSettings = await Should.ThrowAsync<EngineConfigurationException>(() => h.Engine.PutDeviceAsync(withoutHost));

        duplicateTag.Errors.ShouldContain(e => e.Contains("repetido", StringComparison.Ordinal));
        nameTaken.Errors.ShouldContain(e => e.Contains("nombre repetido", StringComparison.Ordinal));
        badSettings.Errors.ShouldContain(e => e.Contains("Falta host", StringComparison.Ordinal));
        h.Engine.GetDevices().Select(d => d.Device).ShouldBe(["plc1", "plc2"]);
        h.Engine.GetTags("plc1").Single().Definition.Key.Tag.ShouldBe("a");
    }

    [Fact]
    public async Task Unknown_original_name_is_not_found()
    {
        await using var h = new EngineHarness();
        await Should.ThrowAsync<KeyNotFoundException>(() => h.Engine.PutDeviceAsync(Device("x"), originalName: "noexiste"));
    }

    [Fact]
    public async Task Deleted_device_disappears_and_is_announced()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0")]), Device("plc2", enabled: false));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);
        using var subscription = h.Engine.Subscribe();

        await h.Engine.DeleteDeviceAsync("PLC1");

        h.Engine.GetDevices().Select(d => d.Device).ShouldBe(["plc2"]);
        h.Engine.GetTags().ShouldBeEmpty();
        h.Engine.SnapshotConfiguration().Devices.Select(d => d.Name).ShouldBe(["plc2"]);
        h.Driver.Connections["plc1"].State.ShouldBe(ConnectionState.Disconnected);
        var events = await DrainAsync(subscription, e => e.OfType<DeviceRemoved>().Any());
        events.OfType<DeviceRemoved>().Single().Device.ShouldBe("plc1");
        await Should.ThrowAsync<KeyNotFoundException>(() => h.Engine.DeleteDeviceAsync("plc1"));
    }

    [Fact]
    public async Task Snapshot_is_an_independent_copy()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0")]));

        var snapshot = h.Engine.SnapshotConfiguration();
        snapshot.Devices[0].Name = "cambiado";
        snapshot.Devices[0].ScanGroups[0].Tags.Clear();
        var device = h.Engine.GetDeviceConfiguration("plc1");
        device.ScanGroups.Clear();

        h.Engine.Configuration.Devices[0].Name.ShouldBe("plc1");
        h.Engine.GetDeviceConfiguration("plc1").ScanGroups[0].Tags.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Browse_uses_the_live_connection()
    {
        await using var h = new EngineHarness(Device("plc1"), Device("plc2", enabled: false));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.Motor"] = 1;
        plc.Values["DB1.Bomba"] = 2;
        plc.Values["DB2.Motor2"] = 3;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Engine.GetDevice("plc1").State == ConnectionState.Connected);

        var result = await h.Engine.BrowseAsync("plc1", new BrowseQuery("motor", MaxResults: 1));

        result.TotalSymbols.ShouldBe(3);
        result.TotalMatches.ShouldBe(2);
        result.Items.ShouldHaveSingleItem().Address.ShouldBe("DB1.Motor");
        await Should.ThrowAsync<InvalidOperationException>(() => h.Engine.BrowseAsync("plc2", new BrowseQuery()));
    }
}
