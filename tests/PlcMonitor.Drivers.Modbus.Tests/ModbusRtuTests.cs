using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.Modbus.Tests;

/// <summary>Modbus RTU contra esclavos simulados (FluentModbus) en un cable serie en memoria, con el bus compartido.</summary>
public sealed class ModbusRtuTests : IAsyncLifetime
{
    private readonly FakeSerialLine _line = new();
    private readonly ModbusRtuServer _slaves = new([3, 4]);
    private readonly SerialBusRegistry _buses;

    public ModbusRtuTests()
    {
        _buses = new SerialBusRegistry(_ => _line.Master);
    }

    public ValueTask InitializeAsync()
    {
        _slaves.Start(_line.Slave);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _slaves.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<ModbusConnection> ConnectAsync(string name, int unit, Action<ModbusSettings>? configure = null)
    {
        var settings = new ModbusSettings { Transport = ModbusTransport.Rtu, SerialPort = "COM-SIM", BaudRate = 115200, UnitId = unit, TimeoutMs = 1000 };
        configure?.Invoke(settings);
        var connection = new ModbusConnection(name, settings, NullLogger.Instance, buses: _buses);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    [Fact]
    public async Task Reads_and_writes_through_the_serial_port()
    {
        _slaves.GetHoldingRegisters(3).SetBigEndian(0, 12.5f);
        await using var c = await ConnectAsync("bomba", 3);

        (await c.ReadAsync(["HR0:FLOAT"], TestContext.Current.CancellationToken))[0].Value.ShouldBe(12.5f);
        (await c.WriteAsync([new("HR10", 42), new("C1", true)], TestContext.Current.CancellationToken)).ShouldAllBe(r => r.Success);

        _slaves.GetHoldingRegisters(3).GetBigEndian<short>(10).ShouldBe((short)42);
        (_slaves.GetCoils(3)[0] & 0b10).ShouldBe(0b10);
        (await c.GetDeviceInfoAsync(TestContext.Current.CancellationToken))["Transport"].ShouldStartWith("Modbus RTU COM-SIM (115200 baudios");
    }

    [Fact]
    public async Task Several_slaves_share_one_port_and_take_turns()
    {
        _slaves.GetHoldingRegisters(3).SetBigEndian<short>(0, 3);
        _slaves.GetHoldingRegisters(4).SetBigEndian<short>(0, 4);
        await using var a = await ConnectAsync("esclavo3", 3);
        await using var b = await ConnectAsync("esclavo4", 4);
        _buses.OpenCount.ShouldBe(1);
        _line.Master.OpenCount.ShouldBe(1);

        // Lecturas simultáneas de los dos dispositivos: el bus las turna y cada respuesta llega a quien la pidió.
        var reads = Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? a : b).ReadAsync(["HR0"], TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(reads);

        for (var i = 0; i < results.Length; i++)
            results[i][0].Value.ShouldBe(i % 2 == 0 ? (short)3 : (short)4);
        (await b.GetDeviceInfoAsync(TestContext.Current.CancellationToken))["SharedBus"].ShouldBe("esclavo3, esclavo4");

        await a.DisconnectAsync(TestContext.Current.CancellationToken);
        _line.Master.IsOpen.ShouldBeTrue();
        await b.DisconnectAsync(TestContext.Current.CancellationToken);
        _line.Master.IsOpen.ShouldBeFalse();
        _buses.OpenCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_slave_that_does_not_answer_does_not_take_down_the_others()
    {
        _slaves.GetHoldingRegisters(3).SetBigEndian<short>(0, 33);
        await using var good = await ConnectAsync("bueno", 3);
        await using var missing = await ConnectAsync("ausente", 9, s => s.TimeoutMs = 200);

        var lost = await missing.ReadAsync(["HR0"], TestContext.Current.CancellationToken);

        lost[0].Quality.ShouldBe(QualityCode.BadNotConnected);
        lost[0].ErrorDetail!.ShouldContain("El esclavo 9 no respondió en 200 ms");
        missing.State.ShouldBe(ConnectionState.Faulted);
        (await good.ReadAsync(["HR0"], TestContext.Current.CancellationToken))[0].Value.ShouldBe((short)33);
        _line.Master.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Slaves_on_one_port_must_share_the_line_settings()
    {
        await using var a = await ConnectAsync("a", 3);
        var settings = new ModbusSettings { Transport = ModbusTransport.Rtu, SerialPort = "COM-SIM", BaudRate = 9600, UnitId = 4 };
        await using var b = new ModbusConnection("b", settings, NullLogger.Instance, buses: _buses);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => b.ConnectAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("ya lo usa(n) a con 115200 baudios");
        b.State.ShouldBe(ConnectionState.Faulted);
        a.State.ShouldBe(ConnectionState.Connected);
    }

    [Fact]
    public void Inter_frame_silence_follows_the_baud_rate()
    {
        SerialBus.SilenceOf(9600).ShouldBe(TimeSpan.FromMilliseconds(5));
        SerialBus.SilenceOf(19200).ShouldBe(TimeSpan.FromMilliseconds(3));
        SerialBus.SilenceOf(115200).ShouldBe(TimeSpan.FromMilliseconds(1.75));
    }
}
