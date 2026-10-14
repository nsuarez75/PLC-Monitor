using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Engine.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static PlcMonitor.Engine.Tests.EngineHarness;

namespace PlcMonitor.Engine.Tests;

public class TagEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enabled_device_connects_and_publishes_values()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "velocidad")]));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 42;

        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "velocidad").Quality == QualityCode.Good);

        h.Value("plc1", "velocidad").Value.ShouldBe(42);
        var status = h.Engine.GetDevice("PLC1");
        status.Enabled.ShouldBeTrue();
        status.State.ShouldBe(ConnectionState.Connected);
    }

    [Fact]
    public async Task Disabled_device_is_not_connected()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false, tags: [Tag("DB1.DBW0")]));

        await h.Engine.StartAsync();

        h.Driver.Plc("plc1").ConnectCount.ShouldBe(0);
        var status = h.Engine.GetDevice("plc1");
        status.Enabled.ShouldBeFalse();
        status.State.ShouldBe(ConnectionState.Disconnected);
        var value = h.Value("plc1", "DB1.DBW0");
        value.Quality.ShouldBe(QualityCode.BadNotConnected);
        value.ErrorDetail.ShouldBe("Dispositivo deshabilitado");
    }

    [Fact]
    public async Task Value_changes_are_picked_up_on_the_next_scan()
    {
        await using var h = new EngineHarness(Device("plc1", intervalMs: 500, tags: [Tag("DB1.DBW0")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);

        plc.Values["DB1.DBW0"] = 2;
        await h.AdvanceUntilAsync(() => Equals(h.Value("plc1", "DB1.DBW0").Value, 2), stepMs: 500);
    }

    [Fact]
    public async Task Lost_connection_marks_tags_bad_and_reconnects_with_backoff()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 7;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);

        plc.Unplugged = true;
        await h.AdvanceUntilAsync(() => h.Engine.GetDevice("plc1").State == ConnectionState.Faulted);

        var value = h.Value("plc1", "DB1.DBW0");
        value.Quality.ShouldBe(QualityCode.BadNotConnected);
        value.ErrorDetail.ShouldBe("Cable desconectado");
        var status = h.Engine.GetDevice("plc1");
        status.Detail.ShouldBe("Cable desconectado");
        status.NextRetry.ShouldNotBeNull();

        plc.Unplugged = false;
        await h.AdvanceUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);
        h.Engine.GetDevice("plc1").State.ShouldBe(ConnectionState.Connected);
        plc.ConnectCount.ShouldBe(2);
    }

    [Fact]
    public async Task Failed_connections_are_retried_with_growing_delays()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        plc.FailConnects = 3;
        var delays = new List<TimeSpan>();
        using var sub = h.Engine.Subscribe();

        await h.Engine.StartAsync();
        await h.AdvanceUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good, stepMs: 250);

        while (sub.Events.TryRead(out var e))
        {
            if (e is DeviceStatusChanged { Status: { State: ConnectionState.Faulted, NextRetry: { } next } status })
                delays.Add(next - status.Timestamp);
        }
        delays.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)]);
        plc.ConnectCount.ShouldBe(4);
    }

    [Fact]
    public async Task Device_can_be_disabled_and_enabled_while_running()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0")]), Device("plc2", tags: [Tag("DB1.DBW0")]));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        h.Driver.Plc("plc2").Values["DB1.DBW0"] = 2;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Engine.GetTags().All(t => t.Value.Quality == QualityCode.Good));

        var status = await h.Engine.SetDeviceEnabledAsync("plc1", false);

        status.Enabled.ShouldBeFalse();
        status.State.ShouldBe(ConnectionState.Disconnected);
        h.Configuration.Devices[0].Enabled.ShouldBeFalse();
        h.Driver.Connections["plc1"].State.ShouldBe(ConnectionState.Disconnected);
        h.Value("plc1", "DB1.DBW0").ErrorDetail.ShouldBe("Dispositivo deshabilitado");
        h.Value("plc2", "DB1.DBW0").Quality.ShouldBe(QualityCode.Good);

        await h.Engine.SetDeviceEnabledAsync("plc1", true);
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);
        h.Configuration.Devices[0].Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Enabling_twice_or_disabling_a_stopped_device_is_harmless()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false, tags: [Tag("DB1.DBW0")]));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();

        await h.Engine.SetDeviceEnabledAsync("plc1", false);
        await h.Engine.SetDeviceEnabledAsync("plc1", true);
        await h.Engine.SetDeviceEnabledAsync("plc1", true);
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);

        h.Driver.Plc("plc1").ConnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task Four_devices_run_independently()
    {
        var names = new[] { "a", "b", "c", "d" };
        await using var h = new EngineHarness([.. names.Select(n => Device(n, tags: [Tag("DB1.DBW0")]))]);
        foreach (var n in names)
            h.Driver.Plc(n).Values["DB1.DBW0"] = n;
        h.Driver.Plc("c").FailConnects = int.MaxValue;

        await h.Engine.StartAsync();
        await WaitUntilAsync(() => names.Where(n => n != "c").All(n => h.Value(n, "DB1.DBW0").Quality == QualityCode.Good));
        await WaitUntilAsync(() => h.Engine.GetDevice("c").State == ConnectionState.Faulted);

        h.Engine.GetDevices().Select(d => d.State).ShouldBe(
            [ConnectionState.Connected, ConnectionState.Connected, ConnectionState.Faulted, ConnectionState.Connected]);
        h.Value("d", "DB1.DBW0").Value.ShouldBe("d");
    }

    [Fact]
    public async Task Invalid_address_gets_bad_configuration_and_does_not_block_other_tags()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("BAD.X"), Tag("DB1.DBW0")]));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 5;

        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);

        var bad = h.Value("plc1", "BAD.X");
        bad.Quality.ShouldBe(QualityCode.BadConfiguration);
        bad.ErrorDetail.ShouldBe("dirección mala");
    }

    [Fact]
    public async Task Invalid_address_keeps_bad_configuration_after_a_reconnect()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("BAD.X"), Tag("DB1.DBW0")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 5;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);

        plc.Unplugged = true;
        await h.AdvanceUntilAsync(() => h.Engine.GetDevice("plc1").State == ConnectionState.Faulted);

        h.Value("plc1", "BAD.X").Quality.ShouldBe(QualityCode.BadConfiguration);
    }

    [Fact]
    public async Task Two_tags_with_the_same_address_both_get_values()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "a"), Tag("DB1.DBW0", "b")]));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 3;

        await h.Engine.StartAsync();

        await WaitUntilAsync(() => h.Engine.GetTags().All(t => Equals(t.Value.Value, 3)));
    }

    [Fact]
    public async Task Subscription_starts_with_a_snapshot_and_then_streams_changes()
    {
        await using var h = new EngineHarness(Device("plc1", intervalMs: 100, tags: [Tag("DB1.DBW0")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);

        using var sub = h.Engine.Subscribe();
        sub.InitialTags.ShouldHaveSingleItem().Value.Value.ShouldBe(1);
        sub.InitialDevices.ShouldHaveSingleItem().State.ShouldBe(ConnectionState.Connected);

        plc.Values["DB1.DBW0"] = 2;
        await h.AdvanceUntilAsync(() => Equals(h.Value("plc1", "DB1.DBW0").Value, 2));

        sub.Events.TryRead(out var e).ShouldBeTrue();
        var update = e.ShouldBeOfType<TagValuesChanged>().Updates.ShouldHaveSingleItem();
        update.Key.ShouldBe(new TagKey("plc1", "DB1.DBW0"));
        update.Value.Value.ShouldBe(2);
    }

    [Fact]
    public async Task Unchanged_values_are_not_broadcast()
    {
        await using var h = new EngineHarness(Device("plc1", intervalMs: 100, tags: [Tag("DB1.DBW0")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);
        using var sub = h.Engine.Subscribe();

        var reads = plc.ReadCount;
        await h.AdvanceUntilAsync(() => plc.ReadCount >= reads + 3, stepMs: 100);

        sub.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Slow_subscriber_is_disconnected_instead_of_blocking_the_engine()
    {
        await using var h = new EngineHarness(Device("plc1", intervalMs: 100, tags: [Tag("DB1.DBW0")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 0;
        using var slow = h.Engine.Subscribe(capacity: 2);

        await h.Engine.StartAsync();
        for (var i = 1; i <= 5; i++)
        {
            plc.Values["DB1.DBW0"] = i;
            var expected = i;
            await h.AdvanceUntilAsync(() => Equals(h.Value("plc1", "DB1.DBW0").Value, expected));
        }

        await Should.ThrowAsync<SubscriberOverflowException>(async () =>
        {
            await foreach (var _ in slow.Events.ReadAllAsync(Ct))
            {
            }
        });
        h.Value("plc1", "DB1.DBW0").Value.ShouldBe(5);
    }

    [Fact]
    public async Task Disposed_subscription_stops_receiving()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0")]));
        var sub = h.Engine.Subscribe();
        sub.Dispose();

        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);

        sub.Events.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Stop_completes_subscriptions()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0")]));
        using var sub = h.Engine.Subscribe();
        await h.Engine.StartAsync();

        await h.Engine.StopAsync();

        // El canal se cierra después de los eventos pendientes: leerlos todos termina sin error.
        await foreach (var _ in sub.Events.ReadAllAsync(Ct).WithCancellation(Ct))
        {
        }
        sub.Events.Completion.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task Writes_need_both_keys()
    {
        await using var h = new EngineHarness(
            Device("cerrado", writesEnabled: false, tags: [Tag("DB1.DBW0", writable: true)]),
            Device("abierto", writesEnabled: true, tags: [Tag("DB1.DBW0", "ro"), Tag("DB1.DBW2", "rw", writable: true)]));
        foreach (var plc in new[] { "cerrado", "abierto" })
        {
            h.Driver.Plc(plc).Values["DB1.DBW0"] = 0;
            h.Driver.Plc(plc).Values["DB1.DBW2"] = 0;
        }
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Engine.GetTags().All(t => t.Value.Quality == QualityCode.Good));

        var deviceClosed = await h.Engine.WriteAsync("cerrado", "DB1.DBW0", "5", "test", Ct);
        var tagReadOnly = await h.Engine.WriteAsync("abierto", "ro", "5", "test", Ct);
        var allowed = await h.Engine.WriteAsync("abierto", "rw", "5", "test", Ct);

        deviceClosed.Quality.ShouldBe(QualityCode.BadWriteDisabled);
        tagReadOnly.Quality.ShouldBe(QualityCode.BadWriteDisabled);
        allowed.Quality.ShouldBe(QualityCode.Good);
        h.Driver.Plc("cerrado").Writes.ShouldBeEmpty();
        var write = h.Driver.Plc("abierto").Writes.ShouldHaveSingleItem();
        write.Address.ShouldBe("DB1.DBW2");
        write.Value.ShouldBe(5);
    }

    [Fact]
    public async Task Write_uses_the_configured_data_type()
    {
        await using var h = new EngineHarness(Device("plc1", writesEnabled: true,
            tags: [Tag("DB1.DBX0.0", "marcha", writable: true, dataType: TagDataType.Boolean)]));
        h.Driver.Plc("plc1").Values["DB1.DBX0.0"] = false;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Engine.GetDevice("plc1").State == ConnectionState.Connected);

        (await h.Engine.WriteAsync("plc1", "marcha", "true", "test", Ct)).Success.ShouldBeTrue();

        h.Driver.Plc("plc1").Writes.ShouldHaveSingleItem().Value.ShouldBe(true);
    }

    [Fact]
    public async Task Write_falls_back_to_the_type_given_by_the_driver()
    {
        await using var h = new EngineHarness(Device("plc1", writesEnabled: true, tags: [Tag("REAL1", writable: true)]));
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Engine.GetDevice("plc1").State == ConnectionState.Connected);

        (await h.Engine.WriteAsync("plc1", "REAL1", "1.5", "test", Ct)).Success.ShouldBeTrue();

        h.Driver.Plc("plc1").Writes.ShouldHaveSingleItem().Value.ShouldBe(1.5f);
    }

    [Fact]
    public async Task Write_with_an_unparseable_value_is_rejected()
    {
        await using var h = new EngineHarness(Device("plc1", writesEnabled: true, tags: [Tag("DB1.DBW0", writable: true)]));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 0;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);

        var result = await h.Engine.WriteAsync("plc1", "DB1.DBW0", "mucho", "test", Ct);

        result.Quality.ShouldBe(QualityCode.BadTypeMismatch);
        h.Driver.Plc("plc1").Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_to_unknown_tag_or_disconnected_device_fails_cleanly()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false, writesEnabled: true, tags: [Tag("DB1.DBW0", writable: true)]));
        await h.Engine.StartAsync();

        (await h.Engine.WriteAsync("plc1", "nada", "1", "test", Ct)).Quality.ShouldBe(QualityCode.BadAddressInvalid);
        (await h.Engine.WriteAsync("plc1", "DB1.DBW0", "1", "test", Ct)).Quality.ShouldBe(QualityCode.BadNotConnected);
    }

    [Fact]
    public async Task On_demand_read_goes_to_the_device()
    {
        await using var h = new EngineHarness(Device("plc1", tags: []));
        h.Driver.Plc("plc1").Values["MW10"] = 99;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Engine.GetDevice("plc1").State == ConnectionState.Connected);

        var values = await h.Engine.ReadAsync("plc1", ["MW10", "MW12"], Ct);

        values[0].Value.ShouldBe(99);
        values[1].Quality.ShouldBe(QualityCode.BadAddressInvalid);
    }

    [Fact]
    public async Task On_demand_read_of_a_stopped_device_returns_not_connected()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false, tags: []));

        var values = await h.Engine.ReadAsync("plc1", ["MW10"], Ct);

        values.ShouldHaveSingleItem().Quality.ShouldBe(QualityCode.BadNotConnected);
    }

    [Fact]
    public async Task Unknown_device_throws()
    {
        await using var h = new EngineHarness(Device("plc1", tags: []));

        Should.Throw<KeyNotFoundException>(() => h.Engine.GetDevice("otro"));
        await Should.ThrowAsync<KeyNotFoundException>(() => h.Engine.SetDeviceEnabledAsync("otro", true));
    }

    [Fact]
    public async Task Engine_can_be_disposed_twice()
    {
        var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0")]));
        await h.Engine.StartAsync();

        await h.Engine.DisposeAsync();
        await h.Engine.DisposeAsync();
    }

    [Fact]
    public void Invalid_configuration_is_rejected_with_all_errors()
    {
        var config = new AgentConfiguration { Devices = { Device("a/b", tags: []), Device("x", tags: []) } };
        config.Devices[1].Driver = "nope";

        var ex = Should.Throw<EngineConfigurationException>(
            () => new TagEngine(config, new DriverRegistry([new FakeDriverFactory()]), NullLoggerFactory.Instance));

        ex.Errors.Count.ShouldBe(2);
        ex.Message.ShouldContain("driver desconocido 'nope'");
    }
}
