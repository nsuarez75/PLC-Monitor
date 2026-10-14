using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Engine.Tests;

public class ConfigurationTests
{
    private static readonly DriverRegistry Registry = new([new FakeDriverFactory()]);

    private static DeviceConfiguration Device(string name = "plc1") => new()
    {
        Name = name,
        Driver = FakeDriverFactory.Id,
        Settings = FakeDriverFactory.Settings(),
        ScanGroups = { new ScanGroupConfiguration { Name = "rapido", IntervalMs = 100, Tags = { new TagConfiguration { Address = "DB1.DBW0" } } } },
    };

    private static IReadOnlyList<string> Validate(params DeviceConfiguration[] devices) =>
        ConfigurationValidator.Validate(new AgentConfiguration { Devices = [.. devices] }, Registry);

    [Fact]
    public void Valid_configuration_has_no_errors() => Validate(Device("a"), Device("b")).ShouldBeEmpty();

    [Fact]
    public void Empty_configuration_is_valid() => Validate().ShouldBeEmpty();

    [Theory]
    [InlineData("", "falta el nombre")]
    [InlineData("a/b", "no puede contener '/'")]
    public void Device_name_is_checked(string name, string expected) =>
        Validate(Device(name)).ShouldHaveSingleItem().ShouldContain(expected);

    [Fact]
    public void Device_names_must_be_unique_ignoring_case() =>
        Validate(Device("plc"), Device("PLC")).ShouldHaveSingleItem().ShouldContain("nombre repetido");

    [Fact]
    public void Unknown_driver_is_reported()
    {
        var device = Device();
        device.Driver = "modbus";
        Validate(device).ShouldHaveSingleItem().ShouldContain("driver desconocido 'modbus'");
    }

    [Fact]
    public void Driver_settings_are_validated_by_the_driver()
    {
        var device = Device();
        device.Settings = FakeDriverFactory.Settings(host: "");
        Validate(device).ShouldHaveSingleItem().ShouldBe("Dispositivo 'plc1': Falta host.");
    }

    [Fact]
    public void Reconnect_settings_are_checked()
    {
        var device = Device();
        device.Reconnect = new ReconnectSettings { InitialDelayMs = 0, MaxDelayMs = -1, Multiplier = 0.5 };
        Validate(device).Count.ShouldBe(3);
    }

    [Fact]
    public void Scan_interval_has_a_minimum()
    {
        var device = Device();
        device.ScanGroups[0].IntervalMs = 10;
        Validate(device).ShouldHaveSingleItem().ShouldContain("intervalMs debe ser al menos 50");
    }

    [Fact]
    public void Group_names_must_be_unique()
    {
        var device = Device();
        device.ScanGroups.Add(new ScanGroupConfiguration { Name = "RAPIDO" });
        Validate(device).ShouldHaveSingleItem().ShouldContain("nombre de grupo repetido");
    }

    [Fact]
    public void Tag_names_must_be_unique_across_groups_of_a_device()
    {
        var device = Device();
        device.ScanGroups.Add(new ScanGroupConfiguration { Name = "lento", Tags = { new TagConfiguration { Address = "DB1.DBW0" } } });
        Validate(device).ShouldHaveSingleItem().ShouldContain("tag 'DB1.DBW0' repetido");
    }

    [Fact]
    public void Same_address_with_different_names_is_allowed()
    {
        var device = Device();
        device.ScanGroups[0].Tags.Add(new TagConfiguration { Name = "copia", Address = "DB1.DBW0" });
        Validate(device).ShouldBeEmpty();
    }

    [Fact]
    public void Tag_without_address_is_reported()
    {
        var device = Device();
        device.ScanGroups[0].Tags.Add(new TagConfiguration { Name = "vacio" });
        Validate(device).ShouldHaveSingleItem().ShouldContain("sin dirección");
    }

    [Fact]
    public void Json_with_comments_and_defaults_is_parsed()
    {
        const string json = """
            {
              // PLC de pruebas
              "devices": [
                {
                  "name": "s71200",
                  "driver": "s7-absolute",
                  "writesEnabled": true,
                  "settings": { "host": "10.225.188.120", "cpu": "S71200" },
                  "scanGroups": [
                    { "name": "rapido", "intervalMs": 250,
                      "tags": [ { "address": "DB1.DBW0" }, { "name": "marcha", "address": "M0.0", "writable": true, "dataType": "Boolean" }, ] }
                  ]
                }
              ]
            }
            """;

        var config = ConfigurationFile.Parse(json);

        var device = config.Devices.ShouldHaveSingleItem();
        device.Enabled.ShouldBeTrue();
        device.WritesEnabled.ShouldBeTrue();
        device.Reconnect.InitialDelayMs.ShouldBe(1000);
        device.Settings.GetProperty("host").GetString().ShouldBe("10.225.188.120");
        var tags = device.ScanGroups.ShouldHaveSingleItem().Tags;
        tags[0].EffectiveName.ShouldBe("DB1.DBW0");
        tags[0].Writable.ShouldBeFalse();
        tags[1].EffectiveName.ShouldBe("marcha");
        tags[1].DataType.ShouldBe(TagDataType.Boolean);
    }

    [Fact]
    public async Task Save_and_load_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plcmon-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AgentConfiguration { Devices = { Device() } };
            config.Devices[0].Enabled = false;

            await ConfigurationFile.SaveAsync(path, config, TestContext.Current.CancellationToken);
            var loaded = await ConfigurationFile.LoadAsync(path, TestContext.Current.CancellationToken);

            ConfigurationFile.Serialize(loaded).ShouldBe(ConfigurationFile.Serialize(config));
            loaded.Devices[0].Enabled.ShouldBeFalse();
            File.Exists(path + ".tmp").ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Load_reports_the_file_on_syntax_errors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plcmon-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ \"devices\": [ }", TestContext.Current.CancellationToken);
        try
        {
            var ex = await Should.ThrowAsync<System.Text.Json.JsonException>(() => ConfigurationFile.LoadAsync(path, TestContext.Current.CancellationToken));
            ex.Message.ShouldStartWith(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Backoff_grows_exponentially_up_to_the_maximum_and_resets()
    {
        var backoff = new ReconnectBackoff(new ReconnectSettings { InitialDelayMs = 1000, MaxDelayMs = 5000, Multiplier = 2 });

        Enumerable.Range(0, 5).Select(_ => backoff.Next().TotalMilliseconds).ShouldBe([1000, 2000, 4000, 5000, 5000]);
        backoff.Attempts.ShouldBe(5);

        backoff.Reset();
        backoff.Next().ShouldBe(TimeSpan.FromSeconds(1));
    }
}
