using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.Modbus.Tests;

/// <summary>De punta a punta contra un esclavo Modbus TCP simulado en el propio proceso (FluentModbus) en un puerto libre.</summary>
public sealed class ModbusConnectionTests : IAsyncLifetime
{
    private const byte Unit = 1;
    private readonly ModbusTcpServer _server = new();
    private readonly ConcurrentQueue<ModbusFunctionCode> _functions = new();
    private int _port;

    /// <summary>Direcciones que el esclavo no tiene (responde excepción 02).</summary>
    private Func<int, int, bool> _missing = (_, _) => false;

    public ValueTask InitializeAsync()
    {
        _server.AddUnit(Unit);
        _server.RequestValidator = (unit, function, address, quantity) =>
        {
            _functions.Enqueue(function);
            return _missing(address, quantity) ? ModbusExceptionCode.IllegalDataAddress : ModbusExceptionCode.OK;
        };
        _port = FreePort();
        _server.Start(new IPEndPoint(IPAddress.Loopback, _port));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _server.Dispose();
        return ValueTask.CompletedTask;
    }

    private Span<short> Registers => _server.GetHoldingRegisters(Unit);

    private async Task<ModbusConnection> ConnectAsync(Action<ModbusSettings>? configure = null)
    {
        var settings = new ModbusSettings { Host = "127.0.0.1", Port = _port, UnitId = Unit, TimeoutMs = 2000 };
        configure?.Invoke(settings);
        var connection = new ModbusConnection("esclavo", settings, NullLogger.Instance);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    [Fact]
    public async Task Reads_every_table_and_type()
    {
        Registers.SetBigEndian<short>(0, -1234);
        Registers.SetBigEndian(2, 12.5f);
        Registers.SetBigEndian(4, 0x0102030405060708L);
        Registers.SetBigEndian<ushort>(10, 0x0008);
        "HOLA"u8.ToArray().CopyTo(_server.GetHoldingRegisterBuffer(Unit)[40..]);
        _server.GetInputRegisters(Unit).SetBigEndian<ushort>(5, 65000);
        _server.GetCoils(Unit)[0] = 0b0000_0101;
        _server.GetDiscreteInputs(Unit)[0] = 0b0000_0010;
        await using var c = await ConnectAsync();

        var values = await c.ReadAsync(["HR0", "HR2:FLOAT", "HR4:INT64", "HR10.3", "HR20:STRING[4]", "IR5:UINT16", "C0", "C1", "C2", "DI1", "40003:FLOAT"],
            TestContext.Current.CancellationToken);

        values.ShouldAllBe(v => v.Quality == QualityCode.Good);
        values.Select(v => v.Value).ShouldBe([(short)-1234, 12.5f, 0x0102030405060708L, true, "HOLA", (ushort)65000, true, false, true, true, 12.5f]);
        values[1].DataType.ShouldBe(TagDataType.Float);
        // HR0..HR4 y HR10/HR20 van en un bloque (hueco ≤ 8 → HR0-HR9 y HR10-HR21), IR, C y DI en otro cada uno.
        _functions.Count(f => f == ModbusFunctionCode.ReadHoldingRegisters).ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task Device_byte_order_and_one_based_addresses_apply()
    {
        Registers.SetBigEndian(2, 12.5f); // HR2..3 = 4148 0000
        await using var c = await ConnectAsync(s =>
        {
            s.ByteOrder = ModbusByteOrder.CDAB;
            s.OneBasedAddresses = true;
        });
        // Base 1: HR3 es el offset 2. Con CDAB, 4148 0000 se lee como 0000 4148 → no es 12.5; con :ABCD en el tag, sí.
        var values = await c.ReadAsync(["HR3:FLOAT:ABCD", "HR3:FLOAT"], TestContext.Current.CancellationToken);
        values[0].Value.ShouldBe(12.5f);
        values[1].Value.ShouldNotBe(12.5f);
    }

    [Fact]
    public async Task Writes_registers_coils_and_register_bits()
    {
        Registers.SetBigEndian<ushort>(30, 0x0100);
        await using var c = await ConnectAsync();

        var results = await c.WriteAsync(
            [new("HR0", 7), new("HR2:FLOAT:CDAB", "1.5"), new("C3", true), new("C4", "1"), new("HR30.0", true), new("HR40:STRING[4]", "AB")],
            TestContext.Current.CancellationToken);

        results.ShouldAllBe(r => r.Success);
        Registers.GetBigEndian<short>(0).ShouldBe((short)7);
        (await c.ReadAsync(["HR2:FLOAT:CDAB"], TestContext.Current.CancellationToken))[0].Value.ShouldBe(1.5f);
        (_server.GetCoils(Unit)[0] & 0b0001_1000).ShouldBe(0b0001_1000);
        Registers.GetBigEndian<ushort>(30).ShouldBe((ushort)0x0101);
        (await c.ReadAsync(["HR40:STRING[4]"], TestContext.Current.CancellationToken))[0].Value.ShouldBe("AB");
        _functions.ShouldContain(ModbusFunctionCode.WriteSingleRegister);
        _functions.ShouldContain(ModbusFunctionCode.WriteSingleCoil);
        _functions.ShouldContain(ModbusFunctionCode.WriteMultipleRegisters);
    }

    [Fact]
    public async Task Multiple_write_functions_only_when_configured()
    {
        await using var c = await ConnectAsync(s => s.UseSingleWriteFunctions = false);
        (await c.WriteAsync([new("HR0", 1), new("C0", true)], TestContext.Current.CancellationToken)).ShouldAllBe(r => r.Success);
        _functions.ShouldNotContain(ModbusFunctionCode.WriteSingleRegister);
        _functions.ShouldNotContain(ModbusFunctionCode.WriteSingleCoil);
        _functions.ShouldContain(ModbusFunctionCode.WriteMultipleRegisters);
        _functions.ShouldContain(ModbusFunctionCode.WriteMultipleCoils);
        _server.GetCoils(Unit)[0].ShouldBe((byte)1);
    }

    [Fact]
    public async Task Read_only_tables_and_bad_values_are_rejected()
    {
        await using var c = await ConnectAsync();
        var results = await c.WriteAsync([new("IR0", 1), new("DI0", true), new("HR0", 70000), new("HR0:FLOAT", "x"), new("ZZ", 1)],
            TestContext.Current.CancellationToken);
        results.Select(r => r.Quality).ShouldBe([QualityCode.BadAccessDenied, QualityCode.BadAccessDenied, QualityCode.BadTypeMismatch,
            QualityCode.BadTypeMismatch, QualityCode.BadAddressInvalid]);
        results[0].ErrorDetail!.ShouldContain("solo lectura");
    }

    [Fact]
    public async Task Missing_registers_inside_a_block_only_affect_their_tags()
    {
        // El esclavo no tiene HR3: el bloque HR0-HR5 falla y se lee tag a tag.
        _missing = (address, quantity) => address <= 3 && 3 < address + quantity;
        Registers.SetBigEndian<short>(5, 55);
        await using var c = await ConnectAsync();

        var values = await c.ReadAsync(["HR0", "HR3", "HR5"], TestContext.Current.CancellationToken);

        values[0].Quality.ShouldBe(QualityCode.Good);
        values[1].Quality.ShouldBe(QualityCode.BadAddressInvalid);
        values[1].ErrorDetail!.ShouldContain("excepción 02");
        values[2].Value.ShouldBe((short)55);
        c.State.ShouldBe(ConnectionState.Connected);
    }

    [Fact]
    public async Task Invalid_addresses_do_not_reach_the_device()
    {
        await using var c = await ConnectAsync();
        c.ValidateAddress("HR1:FLOAT").ShouldBe(AddressValidation.Valid(TagDataType.Float));
        c.ValidateAddress("DB1.DBW0").IsValid.ShouldBeFalse();
        var values = await c.ReadAsync(["DB1.DBW0"], TestContext.Current.CancellationToken);
        values[0].Quality.ShouldBe(QualityCode.BadAddressInvalid);
        _functions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Losing_the_device_faults_the_connection()
    {
        await using var c = await ConnectAsync(s => s.TimeoutMs = 500);
        _server.Stop();

        var values = await c.ReadAsync(["HR0"], TestContext.Current.CancellationToken);

        values[0].Quality.ShouldBe(QualityCode.BadNotConnected);
        c.State.ShouldBe(ConnectionState.Faulted);
    }

    [Fact]
    public async Task Connect_fails_when_nobody_listens()
    {
        var settings = new ModbusSettings { Host = "127.0.0.1", Port = FreePort(), TimeoutMs = 500 };
        await using var c = new ModbusConnection("nadie", settings, NullLogger.Instance);
        await Should.ThrowAsync<Exception>(() => c.ConnectAsync(TestContext.Current.CancellationToken));
        c.State.ShouldBe(ConnectionState.Faulted);
    }

    [Fact]
    public async Task Device_info_describes_the_link()
    {
        await using var c = await ConnectAsync();
        var info = await c.GetDeviceInfoAsync(TestContext.Current.CancellationToken);
        info["Transport"].ShouldBe($"Modbus TCP 127.0.0.1:{_port}");
        info["UnitId"].ShouldBe("1");
    }

    [Fact]
    public async Task Factory_validates_and_creates_connections()
    {
        var factory = new ModbusDriverFactory();
        factory.TypeId.ShouldBe("modbus");
        factory.ValidateSettings(factory.DefaultSettings()).ShouldContain("Host obligatorio.");
        factory.ValidateSettings(System.Text.Json.JsonSerializer.SerializeToElement(new { transport = "Rtu", unitId = 0 }))
            .ShouldBe(["Puerto serie obligatorio (p.ej. COM3 o /dev/ttyUSB0).", "Unit id fuera de rango (1-247)."]);
        factory.ValidateSettings(System.Text.Json.JsonSerializer.SerializeToElement(new { host = "plc", unitId = 255, maxRegistersPerRead = 126 }))
            .ShouldBe(["Registros por petición fuera de rango (1-125)."]);

        var settings = System.Text.Json.JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = _port });
        await using var c = factory.CreateConnection(new DeviceDefinition("x", "modbus", settings), NullLoggerFactory.Instance);
        await c.ConnectAsync(TestContext.Current.CancellationToken);
        (await c.ReadAsync(["HR0"], TestContext.Current.CancellationToken))[0].Quality.ShouldBe(QualityCode.Good);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
