using PlcMonitor.Core;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Engine.Tests;

/// <summary>Disparo del histórico: condición y validación.</summary>
public class HistoryTriggerTests
{
    private static readonly DriverRegistry Registry = new([new FakeDriverFactory()]);

    [Theory]
    [InlineData(TriggerCondition.Equal, "true", true, true)]
    [InlineData(TriggerCondition.Equal, "true", false, false)]
    [InlineData(TriggerCondition.Equal, "false", false, true)]
    [InlineData(TriggerCondition.Equal, "1", true, true)]
    [InlineData(TriggerCondition.Greater, "10", 10.5, true)]
    [InlineData(TriggerCondition.Greater, "10", 10, false)]
    [InlineData(TriggerCondition.GreaterOrEqual, "10", 10, true)]
    [InlineData(TriggerCondition.Less, "10", 3, true)]
    [InlineData(TriggerCondition.LessOrEqual, "2,5", 2.5f, true)]
    [InlineData(TriggerCondition.NotEqual, "0", 4, true)]
    [InlineData(TriggerCondition.NotEqual, "0", 0, false)]
    [InlineData(TriggerCondition.Equal, "MARCHA", "MARCHA", true)]
    [InlineData(TriggerCondition.NotEqual, "MARCHA", "PARO", true)]
    [InlineData(TriggerCondition.Greater, "MARCHA", "PARO", false)]
    public void Condition_compares_numbers_booleans_and_text(TriggerCondition condition, string target, object value, bool met) =>
        new HistoryTrigger { Condition = condition, Value = target }.IsMet(value).ShouldBe(met);

    [Fact]
    public void A_missing_value_does_not_trigger() => new HistoryTrigger { Condition = TriggerCondition.NotEqual, Value = "1" }.IsMet(null).ShouldBeFalse();

    private static IReadOnlyList<string> Validate(HistoryTrigger trigger)
    {
        var device = EngineHarness.Device("plc1", tags:
        [
            new TagConfiguration { Address = "DB1.DBW0", Name = "valor", History = new TagHistorySettings { Enabled = true, Trigger = trigger } },
            new TagConfiguration { Address = "DB1.DBX2.0", Name = "marcha" },
        ]);
        return ConfigurationValidator.Validate(new AgentConfiguration { Devices = [device] }, Registry);
    }

    [Fact]
    public void A_trigger_on_an_existing_tag_is_valid() =>
        Validate(new HistoryTrigger { Tag = "marcha", Value = "true", HoldMs = 5000 }).ShouldBeEmpty();

    [Theory]
    [InlineData("no_existe", TriggerCondition.Equal, "true", 0, "'no_existe' no existe en el dispositivo")]
    [InlineData("valor", TriggerCondition.Equal, "true", 0, "no puede dispararse a sí mismo")]
    [InlineData("", TriggerCondition.Equal, "true", 0, "falta el tag que dispara")]
    [InlineData("marcha", TriggerCondition.Greater, "mucho", 0, "debe ser un número")]
    [InlineData("marcha", TriggerCondition.Equal, " ", 0, "falta el valor del disparo")]
    [InlineData("marcha", TriggerCondition.Equal, "true", -1, "no puede ser negativo")]
    public void Wrong_triggers_are_reported(string tag, TriggerCondition condition, string value, int holdMs, string expected) =>
        Validate(new HistoryTrigger { Tag = tag, Condition = condition, Value = value, HoldMs = holdMs }).ShouldHaveSingleItem().ShouldContain(expected);

    [Fact]
    public void Trigger_survives_the_json_file()
    {
        var device = EngineHarness.Device("plc1", tags:
        [
            new TagConfiguration { Address = "DB1.DBW0", Name = "valor", History = new TagHistorySettings { Enabled = true, Trigger = new HistoryTrigger { Tag = "marcha", Condition = TriggerCondition.GreaterOrEqual, Value = "3", HoldMs = 2000 } } },
        ]);
        var json = ConfigurationFile.Serialize(new AgentConfiguration { Devices = [device] });
        json.ShouldContain("GreaterOrEqual");
        var trigger = System.Text.Json.JsonSerializer.Deserialize<AgentConfiguration>(json, ConfigurationFile.Options)!.Devices[0].ScanGroups[0].Tags[0].History!.Trigger!;
        (trigger.Tag, trigger.Condition, trigger.Value, trigger.HoldMs).ShouldBe(("marcha", TriggerCondition.GreaterOrEqual, "3", 2000));
    }
}
