namespace PlcMonitor.Drivers.Modbus.Tests;

public class ModbusSerialPortsTests
{
    [Fact]
    public void Stable_and_usb_names_come_first_in_numeric_order()
    {
        ModbusSerialPorts.Sort(["/dev/ttyS1", "/dev/ttyUSB10", "/dev/ttyS0", "/dev/ttyUSB2", "/dev/serial/by-id/usb-FTDI_RS485-if00", "/dev/ttyACM0", "/dev/ttyS0"])
            .ShouldBe(["/dev/serial/by-id/usb-FTDI_RS485-if00", "/dev/ttyACM0", "/dev/ttyUSB2", "/dev/ttyUSB10", "/dev/ttyS0", "/dev/ttyS1"]);
        ModbusSerialPorts.Sort(["COM10", "COM3", "COM1"]).ShouldBe(["COM1", "COM3", "COM10"]);
    }

    [Fact]
    public void Listing_the_ports_of_this_machine_does_not_fail() => ModbusSerialPorts.List().ShouldNotBeNull();
}
