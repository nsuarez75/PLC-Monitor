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
    public async Task Changing_tags_during_a_slow_read_does_not_wait_for_it_nor_publish_its_stale_values()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "x"), Tag("DB1.DBW4", "quitado")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        plc.Values["DB1.DBW2"] = 50;
        plc.Values["DB1.DBW4"] = 7;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "x").Quality == QualityCode.Good);

        // Una lectura se queda esperando al PLC con el valor viejo de la dirección vieja.
        plc.Values["DB1.DBW0"] = 2;
        plc.Values["DB1.DBW4"] = 8;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        plc.ReadGate = gate;
        var reads = plc.ReadCount;
        await h.AdvanceUntilAsync(() => plc.ReadCount > reads);
        using var subscription = h.Engine.Subscribe();

        // Quitar un tag y cambiar la dirección de otro no espera a la lectura colgada.
        var put = h.Engine.PutDeviceAsync(Device("plc1", tags: [Tag("DB1.DBW2", "x")]));
        (await Task.WhenAny(put, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBe(put, "PutDevice no debe esperar a la lectura en curso");
        await put;

        plc.ReadGate = null;
        gate.SetResult();
        await h.AdvanceUntilAsync(() => h.Value("plc1", "x").Value is 50);
        var events = new List<EngineEvent>();
        while (subscription.Events.TryRead(out var pending))
            events.Add(pending);
        var stale = events.OfType<TagValuesChanged>().SelectMany(e => e.Updates).Where(u => Equals(u.Value.Value, 2) || u.Key.Tag == "quitado").ToArray();
        stale.ShouldBeEmpty();
        h.Engine.GetTags("plc1").Select(t => t.Definition.Key.Tag).ShouldBe(["x"]);
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
    public async Task Tag_changes_are_applied_without_reconnecting()
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
        plc.ConnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task Unchanged_tags_keep_their_value_while_others_are_edited()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "fijo"), Tag("DB1.DBW2", "otro")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 7;
        plc.Values["DB1.DBW2"] = 2;
        plc.Values["DB1.DBW4"] = 4;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "fijo").Quality == QualityCode.Good && h.Value("plc1", "otro").Quality == QualityCode.Good);
        using var subscription = h.Engine.Subscribe();

        await h.Engine.PutDeviceAsync(Device("plc1", tags: [Tag("DB1.DBW0", "fijo"), Tag("DB1.DBW4", "otro"), Tag("DB1.DBW2", "nuevo")]));

        var events = await DrainAsync(subscription, e => e.OfType<DeviceConfigured>().Any());
        events.OfType<DeviceConfigured>().Single().Tags.Single(t => t.Definition.Key.Tag == "fijo").Value.Quality.ShouldBe(QualityCode.Good);
        await WaitUntilAsync(() => h.Value("plc1", "otro").Value is 4 && h.Value("plc1", "nuevo").Value is 2);
        h.Value("plc1", "fijo").Value.ShouldBe(7);
        // Ni el dispositivo se desconecta ni el tag que no cambia pasa por mala calidad.
        while (subscription.Events.TryRead(out var pending))
            events.Add(pending);
        events.OfType<DeviceStatusChanged>().ShouldNotContain(e => e.Status.State != ConnectionState.Connected);
        events.OfType<TagValuesChanged>().SelectMany(e => e.Updates).ShouldNotContain(u => u.Key.Tag == "fijo");
        plc.ConnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_renamed_tag_that_reads_the_same_address_keeps_its_value()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "viejo")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 7;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "viejo").Quality == QualityCode.Good);
        var before = h.Value("plc1", "viejo");

        await h.Engine.PutDeviceAsync(Device("plc1", tags: [Tag("DB1.DBW0", "nuevo")]));

        // El mismo valor (con su hora), sin pasar por «esperando la primera lectura».
        h.Value("plc1", "nuevo").ShouldBe(before);
    }

    [Fact]
    public async Task Connection_changes_restart_the_device()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "a")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "a").Quality == QualityCode.Good);

        var changed = Device("plc1", tags: [Tag("DB1.DBW0", "a")]);
        changed.Reconnect.MaxDelayMs = 5000;
        await h.Engine.PutDeviceAsync(changed);

        await WaitUntilAsync(() => plc.ConnectCount == 2);
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
