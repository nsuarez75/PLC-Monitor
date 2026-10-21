using PlcMonitor.Abstractions;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Engine.Tests.EngineHarness;

namespace PlcMonitor.Engine.Tests;

/// <summary>Escalado lineal del valor bruto a unidades de ingeniería.</summary>
public class ScalingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Entrada analógica de un S7: 0–27648 = 0–10 bar.
    private static TagScaling Analog(bool clamp = false) => new() { RawMin = 0, RawMax = 27648, EngMin = 0, EngMax = 10, Clamp = clamp };

    private static TagConfiguration Scaled(TagConfiguration tag, TagScaling? scaling)
    {
        tag.Scaling = scaling;
        return tag;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(13824, 5)]
    [InlineData(27648, 10)]
    [InlineData(-2764.8, -1)]
    [InlineData(30412.8, 11)]
    public void Raw_values_are_scaled_by_the_two_points(double raw, double engineering) =>
        Analog().ToEngineering(raw).ShouldBe(engineering, 1e-9);

    [Fact]
    public void Clamp_limits_to_the_engineering_range_even_when_it_is_inverted()
    {
        var inverted = new TagScaling { RawMin = 0, RawMax = 100, EngMin = 50, EngMax = -50, Clamp = true };

        inverted.ToEngineering(-10).ShouldBe(50);
        inverted.ToEngineering(110).ShouldBe(-50);
        inverted.ToEngineering(25).ShouldBe(25);
        Analog(clamp: true).ToEngineering(30000).ShouldBe(10);
    }

    [Fact]
    public void Inverse_gives_back_the_raw_value() => Analog().ToRaw(5).ShouldBe(13824, 1e-9);

    [Fact]
    public void Scaling_is_validated()
    {
        var registry = new PlcMonitor.Core.DriverRegistry([new FakeDriverFactory()]);
        var withTransform = Scaled(Tag("DB1.DBW8", "transformado"), Analog());
        withTransform.Transform = "estados";
        var configuration = new AgentConfiguration
        {
            Devices =
            [
                Device("plc1", tags:
                [
                    Scaled(Tag("DB1.DBW0", "bruto"), new TagScaling { RawMin = 5, RawMax = 5, EngMin = 0, EngMax = 10 }),
                    Scaled(Tag("DB1.DBW2", "ingenieria"), new TagScaling { RawMin = 0, RawMax = 10, EngMin = 3, EngMax = 3 }),
                    Scaled(Tag("DB1.DBW4", "infinito"), new TagScaling { RawMin = 0, RawMax = double.PositiveInfinity, EngMin = 0, EngMax = 1 }),
                    withTransform,
                    Scaled(Tag("DB1.DBW10", "bien"), Analog()),
                ]),
            ],
            Transforms = [new ValueTransform { Name = "estados", Entries = [new TransformEntry { Value = 0, Text = "Paro" }] }],
        };

        var errors = ConfigurationValidator.Validate(configuration, registry);

        errors.Count.ShouldBe(4);
        errors.ShouldContain(e => e.Contains("tag 'bruto': en el escalado, el bruto mínimo y el máximo no pueden ser iguales", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("tag 'ingenieria': en el escalado, el mínimo y el máximo de ingeniería", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("tag 'infinito': los puntos del escalado deben ser números finitos", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("tag 'transformado': un tag escalado no puede tener transformación", StringComparison.Ordinal));
    }

    [Fact]
    public void Scaling_survives_the_configuration_file()
    {
        var configuration = new AgentConfiguration
        {
            Devices = [Device("plc1", tags: [Scaled(Tag("DB1.DBW0", "presion"), Analog(clamp: true)), Tag("DB1.DBW4", "contador")])],
        };

        var tags = ConfigurationFile.Parse(ConfigurationFile.Serialize(configuration)).Devices[0].ScanGroups[0].Tags;

        tags[0].Scaling.ShouldBe(Analog(clamp: true));
        tags[1].Scaling.ShouldBeNull();
    }

    [Fact]
    public async Task Read_values_arrive_scaled_with_the_raw_value()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Scaled(Tag("DB1.DBW0", "presion"), Analog()), Tag("DB1.DBW0", "bruto")]));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 13824;
        await h.Engine.StartAsync();

        await WaitUntilAsync(() => h.Value("plc1", "presion").Quality == QualityCode.Good && h.Value("plc1", "bruto").Quality == QualityCode.Good);

        var scaled = h.Value("plc1", "presion");
        scaled.Value.ShouldBe(5.0);
        scaled.DataType.ShouldBe(TagDataType.Double);
        scaled.Raw.ShouldNotBeNull().Value.ShouldBe(13824);
        // Otro tag que lee la misma dirección sin escalado ve el bruto.
        h.Value("plc1", "bruto").Value.ShouldBe(13824);
    }

    [Fact]
    public async Task Floats_are_scaled_from_their_decimal_text_and_non_numbers_are_left_alone()
    {
        var percent = new TagScaling { RawMin = 0, RawMax = 1, EngMin = 0, EngMax = 100 };
        await using var h = new EngineHarness(Device("plc1", tags: [Scaled(Tag("REAL1", "real"), percent), Scaled(Tag("DB1.DBX0.0", "marcha"), percent)]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["REAL1"] = 0.1f;
        plc.Values["DB1.DBX0.0"] = true;
        await h.Engine.StartAsync();

        await WaitUntilAsync(() => h.Value("plc1", "real").Quality == QualityCode.Good && h.Value("plc1", "marcha").Quality == QualityCode.Good);

        h.Value("plc1", "real").Value.ShouldBe(10.0);
        var flag = h.Value("plc1", "marcha");
        flag.Value.ShouldBe(true);
        flag.Raw.ShouldBeNull();
    }

    [Fact]
    public async Task Changing_the_scaling_applies_without_reconnecting()
    {
        await using var h = new EngineHarness(Device("plc1", tags: [Tag("DB1.DBW0", "presion")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 13824;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "presion").Quality == QualityCode.Good);

        await h.Engine.PutDeviceAsync(Device("plc1", tags: [Scaled(Tag("DB1.DBW0", "presion"), Analog())]));
        await h.AdvanceUntilAsync(() => Equals(h.Value("plc1", "presion").Value, 5.0));

        await h.Engine.PutDeviceAsync(Device("plc1", tags: [Tag("DB1.DBW0", "presion")]));
        await h.AdvanceUntilAsync(() => Equals(h.Value("plc1", "presion").Value, 13824));

        h.Engine.SnapshotConfiguration().Devices.Single().ScanGroups.Single().Tags.Single().Scaling.ShouldBeNull();
        plc.ConnectCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("5", 13824)]
    [InlineData("5.0001", 13824)]
    [InlineData("0.00025", 1)]
    [InlineData("-1", -2765)]
    public async Task Writes_apply_the_inverse_and_round_integers(string engineering, int raw)
    {
        await using var h = new EngineHarness(Device("plc1", writesEnabled: true, tags: [Scaled(Tag("DB1.DBW0", "presion", writable: true), Analog())]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 0;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "presion").Quality == QualityCode.Good);

        var result = await h.Engine.WriteAsync("plc1", "presion", engineering, "test", Ct);

        result.Success.ShouldBeTrue(result.ErrorDetail);
        plc.Writes.ShouldHaveSingleItem().Value.ShouldBe(raw);
    }

    [Fact]
    public async Task Writes_to_a_float_keep_the_decimals()
    {
        var percent = new TagScaling { RawMin = 0, RawMax = 1, EngMin = 0, EngMax = 100 };
        await using var h = new EngineHarness(Device("plc1", writesEnabled: true, tags: [Scaled(Tag("REAL1", "real", writable: true), percent)]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["REAL1"] = 0f;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "real").Quality == QualityCode.Good);

        (await h.Engine.WriteAsync("plc1", "real", "25", "test", Ct)).Success.ShouldBeTrue();

        plc.Writes.ShouldHaveSingleItem().Value.ShouldBe(0.25f);
    }

    [Fact]
    public async Task With_clamp_writes_outside_the_engineering_range_are_rejected()
    {
        await using var h = new EngineHarness(Device("plc1", writesEnabled: true, tags: [Scaled(Tag("DB1.DBW0", "presion", writable: true), Analog(clamp: true))]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 0;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "presion").Quality == QualityCode.Good);

        var result = await h.Engine.WriteAsync("plc1", "presion", "10.5", "test", Ct);

        result.Success.ShouldBeFalse();
        result.ErrorDetail.ShouldBe("El valor está fuera del rango de ingeniería del tag (0 a 10).");
        plc.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Writes_whose_raw_value_does_not_fit_the_type_are_rejected()
    {
        var huge = new TagScaling { RawMin = 0, RawMax = 1e12, EngMin = 0, EngMax = 1 };
        await using var h = new EngineHarness(Device("plc1", writesEnabled: true, tags: [Scaled(Tag("DB1.DBW0", "presion", writable: true), huge)]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 0;
        await h.Engine.StartAsync();
        await WaitUntilAsync(() => h.Value("plc1", "presion").Quality == QualityCode.Good);

        (await h.Engine.WriteAsync("plc1", "presion", "1", "test", Ct)).Quality.ShouldBe(QualityCode.BadTypeMismatch);
        plc.Writes.ShouldBeEmpty();
    }
}
