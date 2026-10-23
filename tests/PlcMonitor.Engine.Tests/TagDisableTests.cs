using PlcMonitor.Abstractions;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Engine.Tests.EngineHarness;

namespace PlcMonitor.Engine.Tests;

/// <summary>Tags desactivados: siguen configurados pero no se leen, no se historizan, no se publican ni se escriben.</summary>
public class TagDisableTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TagConfiguration Disabled(string address, string? name = null, bool writable = false)
    {
        var tag = Tag(address, name, writable);
        tag.Enabled = false;
        return tag;
    }

    [Fact]
    public void Tags_are_enabled_by_default_and_the_flag_survives_the_file()
    {
        var json = """
            {"devices":[{"name":"plc1","driver":"fake","settings":{},"scanGroups":[{"name":"g","tags":[
              {"address":"DB1.DBW0"},{"address":"DB1.DBW2","enabled":false}]}]}]}
            """;

        var tags = ConfigurationFile.Parse(json).Devices[0].ScanGroups[0].Tags;
        tags[0].Enabled.ShouldBeTrue();
        tags[1].Enabled.ShouldBeFalse();
        var again = ConfigurationFile.Parse(ConfigurationFile.Serialize(ConfigurationFile.Parse(json))).Devices[0].ScanGroups[0].Tags;
        again.Select(t => t.Enabled).ShouldBe([true, false]);
    }

    [Fact]
    public async Task Disabled_tag_is_not_read_and_shows_it()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "activo"), Disabled("DB1.DBW2", "parado")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        plc.Values["DB1.DBW2"] = 2;

        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "activo").Quality == QualityCode.Good);
        await h.AdvanceUntilAsync(() => plc.ReadCount > 3);

        plc.ReadAddresses.Keys.ShouldBe(["DB1.DBW0"]);
        var value = h.Value("plc1", "parado");
        value.Quality.ShouldBe(QualityCode.Bad);
        value.ErrorDetail.ShouldBe("Tag desactivado");
        var definition = h.Engine.GetTags("plc1").Single(t => t.Definition.Key.Tag == "parado").Definition;
        definition.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Stopping_the_device_does_not_overwrite_the_disabled_value()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0"), Disabled("DB1.DBW2", "parado")]));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "DB1.DBW0").Quality == QualityCode.Good);

        await h.Engine.StopAsync();

        h.Value("plc1", "parado").ErrorDetail.ShouldBe("Tag desactivado");
        h.Value("plc1", "DB1.DBW0").Quality.ShouldBe(QualityCode.BadNotConnected);
    }

    [Fact]
    public async Task Disabled_tag_cannot_be_written_even_with_both_keys()
    {
        await using var h = new EngineHarness(Device("plc1", writesEnabled: true, tags: [Disabled("DB1.DBW0", "parado", writable: true)]));
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Engine.GetDevice("plc1").State == ConnectionState.Connected);

        var result = await h.Engine.WriteAsync("plc1", "parado", "5", "test", Ct);

        result.Quality.ShouldBe(QualityCode.BadConfiguration);
        result.ErrorDetail.ShouldBe("El tag está desactivado.");
        h.Driver.Plc("plc1").Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Disabling_and_enabling_is_applied_without_reconnecting()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "a"), Tag("DB1.DBW2", "b")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        plc.Values["DB1.DBW2"] = 2;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "b").Quality == QualityCode.Good);

        await h.Engine.PutDeviceAsync(Device("plc1", tags: [Tag("DB1.DBW0", "a"), Disabled("DB1.DBW2", "b")]));
        h.Value("plc1", "b").ErrorDetail.ShouldBe("Tag desactivado");
        plc.ReadAddresses.Clear();
        await h.AdvanceUntilAsync(() => plc.ReadAddresses.ContainsKey("DB1.DBW0"));
        await h.AdvanceUntilAsync(() => plc.ReadAddresses["DB1.DBW0"] > 2);
        plc.ReadAddresses.ContainsKey("DB1.DBW2").ShouldBeFalse();
        h.Value("plc1", "b").ErrorDetail.ShouldBe("Tag desactivado");

        await h.Engine.PutDeviceAsync(Device("plc1", tags: [Tag("DB1.DBW0", "a"), Tag("DB1.DBW2", "b")]));
        await h.AdvanceUntilAsync(() => h.Value("plc1", "b").Quality == QualityCode.Good);
        h.Value("plc1", "b").Value.ShouldBe(2);
        plc.ConnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_tag_does_not_inherit_the_value_of_a_removed_disabled_one()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Disabled("DB1.DBW0", "viejo")]));
        await h.Engine.StartAsync();

        await h.Engine.PutDeviceAsync(Device("plc1", tags: [Tag("DB1.DBW0", "nuevo")]));

        h.Value("plc1", "nuevo").ErrorDetail.ShouldNotBe("Tag desactivado");
    }

    [Fact]
    public void Disabled_tag_is_neither_historized_nor_published()
    {
        var history = new TagHistorySettings { Enabled = true };
        var definition = new TagDefinition(new TagKey("plc1", "t"), "DB1.DBW0", "g", TimeSpan.FromSeconds(1), Writable: false, DataType: null)
        {
            History = history,
        };

        definition.IsHistorized.ShouldBeTrue();
        definition.IsOpcUaPublished.ShouldBeTrue();
        (definition with { Enabled = false }).IsHistorized.ShouldBeFalse();
        (definition with { Enabled = false }).IsOpcUaPublished.ShouldBeFalse();
    }
}
