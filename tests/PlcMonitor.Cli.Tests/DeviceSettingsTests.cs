using System.Text.Json;
using PlcMonitor.Abstractions;
using PlcMonitor.Drivers.Modbus;
using PlcMonitor.Drivers.S7Absolute;

namespace PlcMonitor.Cli.Tests;

public class DeviceSettingsTests
{
    [Fact]
    public void Modbus_tcp_uses_port_502_by_default()
    {
        var device = new DeviceSettings { Driver = "modbus", Host = "10.0.0.5", UnitId = 7, ByteOrder = ModbusByteOrder.CDAB }.ToDeviceDefinition();
        var settings = device.Settings.Deserialize<ModbusSettings>(DriverJson.Options)!;
        (settings.Transport, settings.Host, settings.Port, settings.UnitId, settings.ByteOrder)
            .ShouldBe((ModbusTransport.Tcp, "10.0.0.5", 502, 7, ModbusByteOrder.CDAB));
        device.Name.ShouldBe("10.0.0.5");
    }

    [Fact]
    public void Modbus_rtu_needs_a_serial_port_instead_of_a_host()
    {
        new DeviceSettings { Driver = "modbus" }.Validate().Successful.ShouldBeFalse();
        var cli = new DeviceSettings { Driver = "modbus", SerialPort = "/dev/ttyUSB0", BaudRate = 19200, Parity = ModbusParity.Even, OneBased = true };
        cli.Validate().Successful.ShouldBeTrue();

        var device = cli.ToDeviceDefinition();
        var settings = device.Settings.Deserialize<ModbusSettings>(DriverJson.Options)!;
        (settings.Transport, settings.SerialPort, settings.BaudRate, settings.Parity, settings.OneBasedAddresses)
            .ShouldBe((ModbusTransport.Rtu, "/dev/ttyUSB0", 19200, ModbusParity.Even, true));
        device.Name.ShouldBe("/dev/ttyUSB0");
        new ModbusDriverFactory().ValidateSettings(device.Settings).ShouldBeEmpty();
    }

    [Fact]
    public void Modbus_rtu_over_tcp()
    {
        var device = new DeviceSettings { Driver = "modbus", Host = "gw", Port = 4001, RtuOverTcp = true }.ToDeviceDefinition();
        var settings = device.Settings.Deserialize<ModbusSettings>(DriverJson.Options)!;
        (settings.Transport, settings.Port).ShouldBe((ModbusTransport.RtuOverTcp, 4001));
    }

    [Fact]
    public void S7_absolute_keeps_port_102()
    {
        var device = new DeviceSettings { Driver = "s7-absolute", Host = "plc" }.ToDeviceDefinition();
        device.Settings.Deserialize<S7AbsoluteSettings>(DriverJson.Options)!.Port.ShouldBe(102);
    }
}
