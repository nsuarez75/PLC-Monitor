using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.Modbus.Tests;

public class ModbusAddressTests
{
    [Theory]
    [InlineData("HR0", ModbusTable.HoldingRegister, 0, ModbusValueType.Int16)]
    [InlineData("hr100:float", ModbusTable.HoldingRegister, 100, ModbusValueType.Float32)]
    [InlineData("IR5:UINT16", ModbusTable.InputRegister, 5, ModbusValueType.UInt16)]
    [InlineData("IR5:WORD", ModbusTable.InputRegister, 5, ModbusValueType.UInt16)]
    [InlineData("HR2:INT32", ModbusTable.HoldingRegister, 2, ModbusValueType.Int32)]
    [InlineData("HR2:DWORD", ModbusTable.HoldingRegister, 2, ModbusValueType.UInt32)]
    [InlineData("HR2:LINT", ModbusTable.HoldingRegister, 2, ModbusValueType.Int64)]
    [InlineData("HR2:UINT64", ModbusTable.HoldingRegister, 2, ModbusValueType.UInt64)]
    [InlineData("HR2:DOUBLE", ModbusTable.HoldingRegister, 2, ModbusValueType.Float64)]
    [InlineData("HR2:REAL", ModbusTable.HoldingRegister, 2, ModbusValueType.Float32)]
    [InlineData("C0", ModbusTable.Coil, 0, ModbusValueType.Bool)]
    [InlineData("C7:BOOL", ModbusTable.Coil, 7, ModbusValueType.Bool)]
    [InlineData("DI3", ModbusTable.DiscreteInput, 3, ModbusValueType.Bool)]
    [InlineData(" HR 65535 ", ModbusTable.HoldingRegister, 65535, ModbusValueType.Int16)]
    public void Parses_table_style(string text, ModbusTable table, int offset, ModbusValueType type)
    {
        var a = ModbusAddress.Parse(text);
        a.Table.ShouldBe(table);
        a.Offset.ShouldBe(offset);
        a.ValueType.ShouldBe(type);
        a.ByteOrder.ShouldBeNull();
    }

    [Theory]
    [InlineData("40001", ModbusTable.HoldingRegister, 0)]
    [InlineData("400001", ModbusTable.HoldingRegister, 0)]
    [InlineData("49999", ModbusTable.HoldingRegister, 9998)]
    [InlineData("465536", ModbusTable.HoldingRegister, 65535)]
    [InlineData("30010", ModbusTable.InputRegister, 9)]
    [InlineData("10001", ModbusTable.DiscreteInput, 0)]
    [InlineData("00001", ModbusTable.Coil, 0)]
    [InlineData("000100", ModbusTable.Coil, 99)]
    public void Parses_modicon_style_always_from_one(string text, ModbusTable table, int offset)
    {
        // Modicon numera desde 1 aunque el dispositivo use base 0 en las direcciones por tabla, y al revés.
        foreach (var oneBased in new[] { false, true })
        {
            var a = ModbusAddress.Parse(text, oneBased);
            a.Table.ShouldBe(table);
            a.Offset.ShouldBe(offset);
        }
    }

    [Fact]
    public void Table_style_follows_the_device_base()
    {
        ModbusAddress.Parse("HR1", oneBased: true).Offset.ShouldBe(0);
        ModbusAddress.Parse("HR1", oneBased: false).Offset.ShouldBe(1);
        ModbusAddress.Parse("HR65536", oneBased: true).Offset.ShouldBe(65535);
        ModbusAddress.TryParse("HR0", oneBased: true, out _, out var error).ShouldBeFalse();
        error.ShouldContain("desde 1");
        ModbusAddress.TryParse("HR65536", oneBased: false, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Parses_type_and_byte_order_in_any_order()
    {
        var a = ModbusAddress.Parse("HR100:FLOAT:CDAB");
        a.ValueType.ShouldBe(ModbusValueType.Float32);
        a.ByteOrder.ShouldBe(ModbusByteOrder.CDAB);
        a.Count.ShouldBe(2);

        var b = ModbusAddress.Parse("HR100:dcba:int32");
        b.ValueType.ShouldBe(ModbusValueType.Int32);
        b.ByteOrder.ShouldBe(ModbusByteOrder.DCBA);

        ModbusAddress.Parse("400101:UINT32:BADC").ShouldBe(new ModbusAddress(ModbusTable.HoldingRegister, 100, ModbusValueType.UInt32, -1, ModbusByteOrder.BADC));
    }

    [Fact]
    public void Parses_register_bits()
    {
        var a = ModbusAddress.Parse("HR10.3");
        a.IsRegisterBit.ShouldBeTrue();
        a.BitIndex.ShouldBe(3);
        a.ValueType.ShouldBe(ModbusValueType.Bool);
        a.DataType.ShouldBe(TagDataType.Boolean);
        a.Count.ShouldBe(1);
        ModbusAddress.Parse("40011.15").BitIndex.ShouldBe(15);
        ModbusAddress.Parse("IR4.0:BOOL").Table.ShouldBe(ModbusTable.InputRegister);
    }

    [Fact]
    public void Parses_strings()
    {
        var a = ModbusAddress.Parse("HR20:STRING[10]");
        a.ValueType.ShouldBe(ModbusValueType.String);
        a.StringLength.ShouldBe(10);
        a.Count.ShouldBe(5);
        ModbusAddress.Parse("HR20:STRING[7]").Count.ShouldBe(4);
        a.DataType.ShouldBe(TagDataType.String);
    }

    [Theory]
    [InlineData("", "vacía")]
    [InlineData("DB1.DBW0", "no es una dirección Modbus")]
    [InlineData("HR", "no es una dirección Modbus")]
    [InlineData("50001", "no es una dirección Modbus")]
    [InlineData("40000", "empieza en 1")]
    [InlineData("HR1:FLOTA", "no es un tipo")]
    [InlineData("HR1:FLOAT:INT16", "tipo aparece dos veces")]
    [InlineData("HR1:ABCD:CDAB", "orden de bytes aparece dos veces")]
    [InlineData("HR1.16", "de 0 a 15")]
    [InlineData("HR1.3:INT16", "no lleva tipo")]
    [InlineData("HR1:BOOL", "indica cuál")]
    [InlineData("C1:INT16", "bits sueltos")]
    [InlineData("C1.2", "bits sueltos")]
    [InlineData("DI1:CDAB", "bits sueltos")]
    [InlineData("HR1:STRING[0]", "entre 1 y 250")]
    [InlineData("HR1:STRING[251]", "entre 1 y 250")]
    [InlineData("HR65535:FLOAT", "se sale del mapa")]
    [InlineData("HR1:1234", "no es un tipo")]
    public void Rejects_invalid_addresses(string text, string message)
    {
        ModbusAddress.TryParse(text, oneBased: false, out _, out var error).ShouldBeFalse();
        error.ShouldContain(message);
    }

    [Theory]
    [InlineData("HR0")]
    [InlineData("HR100:FLOAT:CDAB")]
    [InlineData("IR5:UINT16")]
    [InlineData("HR10.3")]
    [InlineData("HR20:STRING[10]")]
    [InlineData("C4")]
    [InlineData("DI9")]
    [InlineData("HR2:DOUBLE:DCBA")]
    [InlineData("HR2:UINT64")]
    public void Format_round_trips(string text)
    {
        var a = ModbusAddress.Parse(text);
        a.Format().ShouldBe(text);
        ModbusAddress.Parse(a.Format(oneBased: true), oneBased: true).ShouldBe(a);
    }
}
