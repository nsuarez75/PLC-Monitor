using PlcMonitor.Abstractions;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Engine.Tests.EngineHarness;

namespace PlcMonitor.Engine.Tests;

/// <summary>Transformaciones de valores y grupos de visualización de los tags.</summary>
public class TransformTests
{
    private static ValueTransform Estados(string name = "Estados") => new()
    {
        Name = name,
        Entries = [new TransformEntry { Value = 0, Text = "Parado" }, new TransformEntry { Value = 1, Text = "Marcha" }],
    };

    private static TagConfiguration With(TagConfiguration tag, string? transform = null, string? folder = null)
    {
        tag.Transform = transform;
        tag.Folder = folder;
        return tag;
    }

    [Fact]
    public void Transforms_are_validated()
    {
        var registry = new PlcMonitor.Core.DriverRegistry([new FakeDriverFactory()]);
        var repeated = new ValueTransform { Name = "estados", Entries = [new TransformEntry { Value = 1, Text = "A" }, new TransformEntry { Value = 1, Text = "" }] };
        var configuration = new AgentConfiguration
        {
            Transforms = [Estados(), repeated, new ValueTransform { Name = " " }],
            Devices = [Device("plc1", tags: [With(Tag("DB1.DBW0", "a"), transform: "NoExiste")])],
        };

        var errors = ConfigurationValidator.Validate(configuration, registry);

        errors.ShouldContain(e => e.Contains("'estados': nombre repetido", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("el valor 1 está repetido", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("falta el texto del valor 1", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("Transformación #3: falta el nombre", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("la transformación 'NoExiste' no existe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transforms_are_replaced_announced_and_in_the_snapshot()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false));
        await h.Engine.StartAsync();
        using var subscription = h.Engine.Subscribe();
        subscription.InitialTransforms.ShouldBeEmpty();

        var saved = await h.Engine.SetTransformsAsync([Estados(" Estados ")]);

        saved.ShouldHaveSingleItem().Name.ShouldBe("Estados");
        h.Engine.GetTransforms().Single().Entries.Select(e => (e.Value, e.Text)).ShouldBe([(0L, "Parado"), (1L, "Marcha")]);
        h.Engine.SnapshotConfiguration().Transforms.ShouldHaveSingleItem().Name.ShouldBe("Estados");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changed = (TransformsChanged)await subscription.Events.ReadAsync(cts.Token);
        changed.Transforms.ShouldHaveSingleItem().Name.ShouldBe("Estados");
        using var later = h.Engine.Subscribe();
        later.InitialTransforms.ShouldHaveSingleItem().Name.ShouldBe("Estados");
    }

    [Fact]
    public async Task A_transform_in_use_cannot_be_removed_and_invalid_ones_change_nothing()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false));
        await h.Engine.StartAsync();
        await h.Engine.SetTransformsAsync([Estados()]);
        await h.Engine.PutDeviceAsync(Device("plc1", enabled: false, tags: [With(Tag("DB1.DBW0", "motor"), transform: "estados")]));

        var inUse = await Should.ThrowAsync<EngineConfigurationException>(() => h.Engine.SetTransformsAsync([]));
        var invalid = await Should.ThrowAsync<EngineConfigurationException>(() => h.Engine.SetTransformsAsync([Estados(), Estados()]));

        inUse.Errors.ShouldHaveSingleItem().ShouldContain("'estados' la usan plc1/motor");
        invalid.Errors.ShouldContain(e => e.Contains("nombre repetido", StringComparison.Ordinal));
        h.Engine.GetTransforms().ShouldHaveSingleItem().Name.ShouldBe("Estados");
    }

    [Fact]
    public async Task A_tag_with_an_unknown_transform_is_rejected()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false));
        await h.Engine.StartAsync();

        var ex = await Should.ThrowAsync<EngineConfigurationException>(
            () => h.Engine.PutDeviceAsync(Device("plc1", enabled: false, tags: [With(Tag("DB1.DBW0", "motor"), transform: "Estados")])));

        ex.Errors.ShouldContain(e => e.Contains("la transformación 'Estados' no existe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_transform_or_folder_keeps_the_value_and_reaches_subscribers()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "motor")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        await h.Engine.StartAsync();
        await h.Engine.SetTransformsAsync([Estados()]);
        await WaitUntilAsync(() => h.Value("plc1", "motor").Quality == QualityCode.Good);
        using var subscription = h.Engine.Subscribe();

        await h.Engine.PutDeviceAsync(Device("plc1", tags: [With(Tag("DB1.DBW0", "motor"), transform: "Estados", folder: "Cinta 1")]));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        DeviceConfigured? configured = null;
        while (configured is null)
            configured = await subscription.Events.ReadAsync(cts.Token) as DeviceConfigured;
        var tag = configured.Tags.ShouldHaveSingleItem();
        (tag.Definition.Transform, tag.Definition.Folder).ShouldBe(("Estados", "Cinta 1"));
        tag.Value.Quality.ShouldBe(QualityCode.Good);
        var saved = h.Engine.SnapshotConfiguration().Devices.Single().ScanGroups.Single().Tags.Single();
        (saved.Transform, saved.Folder).ShouldBe(("Estados", "Cinta 1"));
        plc.ConnectCount.ShouldBe(1);
    }
}
