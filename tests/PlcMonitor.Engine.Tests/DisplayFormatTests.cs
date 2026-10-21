using PlcMonitor.Abstractions;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Engine.Tests.EngineHarness;

namespace PlcMonitor.Engine.Tests;

/// <summary>Unidad y decimales con los que los clientes muestran los tags.</summary>
public class DisplayFormatTests
{
    private static TagConfiguration With(TagConfiguration tag, string? unit, int? decimals)
    {
        tag.Unit = unit;
        tag.Decimals = decimals;
        return tag;
    }

    [Fact]
    public void Decimals_and_unit_are_validated()
    {
        var registry = new PlcMonitor.Core.DriverRegistry([new FakeDriverFactory()]);
        var configuration = new AgentConfiguration
        {
            Devices =
            [
                Device("plc1", tags:
                [
                    With(Tag("DB1.DBD0", "negativo"), unit: null, decimals: -1),
                    With(Tag("DB1.DBD4", "muchos"), unit: null, decimals: ConfigurationValidator.MaxDecimals + 1),
                    With(Tag("DB1.DBD8", "larga"), unit: new string('m', ConfigurationValidator.MaxUnitLength + 1), decimals: null),
                    With(Tag("DB1.DBD12", "bien"), unit: "bar", decimals: ConfigurationValidator.MaxDecimals),
                ]),
            ],
        };

        var errors = ConfigurationValidator.Validate(configuration, registry);

        errors.Count.ShouldBe(3);
        errors.ShouldContain(e => e.Contains("tag 'negativo': los decimales deben estar entre 0", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("tag 'muchos': los decimales deben estar entre 0", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("tag 'larga': la unidad no puede tener más de", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_unit_or_decimals_keeps_the_connection_and_reaches_subscribers()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "presion")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "presion").Quality == QualityCode.Good);
        using var subscription = h.Engine.Subscribe();

        await h.Engine.PutDeviceAsync(Device("plc1", tags: [With(Tag("DB1.DBW0", "presion"), unit: "bar", decimals: 2)]));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        DeviceConfigured? configured = null;
        while (configured is null)
            configured = await subscription.Events.ReadAsync(cts.Token) as DeviceConfigured;
        var tag = configured.Tags.ShouldHaveSingleItem();
        (tag.Definition.Unit, tag.Definition.Decimals).ShouldBe(("bar", 2));
        tag.Value.Quality.ShouldBe(QualityCode.Good);
        var saved = h.Engine.SnapshotConfiguration().Devices.Single().ScanGroups.Single().Tags.Single();
        (saved.Unit, saved.Decimals).ShouldBe(("bar", 2));
        plc.ConnectCount.ShouldBe(1);
    }

    [Fact]
    public void Unit_and_decimals_survive_the_configuration_file()
    {
        var configuration = new AgentConfiguration
        {
            Devices = [Device("plc1", tags: [With(Tag("DB1.DBD0", "presion"), unit: "bar", decimals: 0), Tag("DB1.DBW4", "contador")])],
        };

        var json = ConfigurationFile.Serialize(configuration);
        var tags = ConfigurationFile.Parse(json).Devices[0].ScanGroups[0].Tags;

        (tags[0].Unit, tags[0].Decimals).ShouldBe(("bar", 0));
        (tags[1].Unit, tags[1].Decimals).ShouldBe((null, null));
    }
}
