using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Core.Tests;

public class DriverRegistryTests
{
    private sealed class DummySettings
    {
        public string Host { get; set; } = "";

        public int Port { get; set; } = 502;
    }

    private sealed class DummyFactory(string typeId = "dummy") : DriverFactoryBase<DummySettings>
    {
        public override string TypeId { get; } = typeId;

        public override string DisplayName => "Dummy";

        public override DriverCapabilities Capabilities => DriverCapabilities.Read;

        protected override IReadOnlyList<string> Validate(DummySettings settings) =>
            string.IsNullOrWhiteSpace(settings.Host) ? ["host obligatorio"] : [];

        protected override IDeviceConnection CreateConnection(string deviceName, DummySettings settings, ILoggerFactory loggerFactory) =>
            new FakeDeviceConnection(deviceName);
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        var registry = new DriverRegistry([new DummyFactory("s7-symbolic")]);
        registry.Get("S7-Symbolic").TypeId.ShouldBe("s7-symbolic");
    }

    [Fact]
    public void Unknown_driver_lists_available_ones()
    {
        var registry = new DriverRegistry([new DummyFactory("b"), new DummyFactory("a")]);
        var ex = Should.Throw<KeyNotFoundException>(() => registry.Get("modbus-tcp"));
        ex.Message.ShouldContain("a, b");
    }

    [Fact]
    public void Duplicate_type_id_is_rejected()
    {
        var registry = new DriverRegistry([new DummyFactory("x")]);
        Should.Throw<InvalidOperationException>(() => registry.Register(new DummyFactory("X")));
    }

    [Fact]
    public void Factory_validates_settings_before_creating_connection()
    {
        var factory = new DummyFactory();
        var bad = new DeviceDefinition("PLC1", "dummy", JsonSerializer.SerializeToElement(new { host = "" }));
        Should.Throw<ArgumentException>(() => factory.CreateConnection(bad, NullLoggerFactory.Instance)).Message.ShouldContain("host obligatorio");

        var good = new DeviceDefinition("PLC1", "dummy", JsonSerializer.SerializeToElement(new { host = "10.0.0.1" }));
        factory.CreateConnection(good, NullLoggerFactory.Instance).DeviceName.ShouldBe("PLC1");
    }

    [Fact]
    public void Factory_rejects_device_of_another_driver()
    {
        var factory = new DummyFactory();
        var device = new DeviceDefinition("PLC1", "otro", JsonSerializer.SerializeToElement(new { host = "h" }));
        Should.Throw<ArgumentException>(() => factory.CreateConnection(device, NullLoggerFactory.Instance));
    }

    [Fact]
    public void Default_settings_round_trip()
    {
        var factory = new DummyFactory();
        var defaults = factory.DefaultSettings();
        defaults.GetProperty("port").GetInt32().ShouldBe(502);
        factory.ValidateSettings(defaults).ShouldContain("host obligatorio");
    }

    [Fact]
    public void Malformed_settings_are_reported_not_thrown()
    {
        var factory = new DummyFactory();
        factory.ValidateSettings(JsonSerializer.SerializeToElement(new { port = "no-es-numero" })).ShouldHaveSingleItem().ShouldStartWith("Ajustes no válidos");
    }
}
