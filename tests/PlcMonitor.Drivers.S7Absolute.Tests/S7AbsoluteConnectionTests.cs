using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.S7Absolute.Tests;

public class S7AbsoluteConnectionTests
{
    /// <summary>PLC simulado en memoria: un array de bytes por área/DB.</summary>
    private sealed class FakeTransport : IS7Transport
    {
        public Dictionary<(S7Area, int), byte[]> Memory { get; } = new();

        public List<(S7Area Area, int Db, int Start, int Count)> Reads { get; } = [];

        public Exception? NextReadException { get; set; }

        public bool IsConnected { get; private set; }

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public void Close() => IsConnected = false;

        public Task<byte[]> ReadBytesAsync(S7Area area, int db, int start, int count, CancellationToken cancellationToken)
        {
            Reads.Add((area, db, start, count));
            if (NextReadException is { } ex)
            {
                NextReadException = null;
                throw ex;
            }
            if (!Memory.TryGetValue((area, db), out var mem) || start + count > mem.Length)
                throw new InvalidOperationException($"Área {area} DB{db} no existe o rango fuera de límites");
            return Task.FromResult(mem.AsSpan(start, count).ToArray());
        }

        public Task WriteBytesAsync(S7Area area, int db, int start, byte[] data, CancellationToken cancellationToken)
        {
            data.CopyTo(Memory[(area, db)], start);
            return Task.CompletedTask;
        }

        public Task WriteBitAsync(S7Area area, int db, int start, int bit, bool value, CancellationToken cancellationToken)
        {
            var mem = Memory[(area, db)];
            mem[start] = value ? (byte)(mem[start] | (1 << bit)) : (byte)(mem[start] & ~(1 << bit));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["Cpu"] = "Fake" });

        public void Dispose() => IsConnected = false;
    }

    private static (S7AbsoluteConnection Conn, FakeTransport Plc) Create()
    {
        var plc = new FakeTransport();
        plc.Memory[(S7Area.DataBlock, 1)] = new byte[64];
        plc.Memory[(S7Area.Merker, 0)] = new byte[16];
        var conn = new S7AbsoluteConnection("PLC1", new S7AbsoluteSettings { Host = "sim" }, NullLogger.Instance, () => plc);
        return (conn, plc);
    }

    [Fact]
    public async Task Read_before_connect_returns_not_connected()
    {
        var (conn, plc) = Create();
        var values = await conn.ReadAsync(["DB1.DBW0"]);
        values.ShouldHaveSingleItem().Quality.ShouldBe(QualityCode.BadNotConnected);
        plc.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_decoded_values_with_one_request_per_block()
    {
        var (conn, plc) = Create();
        var db = plc.Memory[(S7Area.DataBlock, 1)];
        db[0] = 0x01; db[1] = 0x02;          // DBW0 = 0x0102
        db[4] = 0b0000_0100;                 // DBX4.2
        new byte[] { 0x41, 0x20, 0, 0 }.CopyTo(db, 8); // REAL8 = 10.0
        await conn.ConnectAsync();

        var values = await conn.ReadAsync(["DB1.DBW0", "DB1.DBX4.2", "DB1.REAL8"]);

        values.Select(v => v.Value).ShouldBe([(ushort)0x0102, true, 10f]);
        values.ShouldAllBe(v => v.Quality == QualityCode.Good);
        plc.Reads.ShouldHaveSingleItem().ShouldBe((S7Area.DataBlock, 1, 0, 12));
    }

    [Fact]
    public async Task Invalid_address_only_affects_its_own_item()
    {
        var (conn, _) = Create();
        await conn.ConnectAsync();
        var values = await conn.ReadAsync(["DB1.DBW0", "DB1.QUE0", "DB99.DBW0"]);
        values[0].Quality.ShouldBe(QualityCode.Good);
        values[1].Quality.ShouldBe(QualityCode.BadAddressInvalid);
        values[2].Quality.ShouldBe(QualityCode.BadAddressInvalid);
        values[2].ErrorDetail!.ShouldContain("no existe");
    }

    [Fact]
    public async Task Put_get_disabled_is_reported_as_access_denied()
    {
        var (conn, plc) = Create();
        await conn.ConnectAsync();

        plc.NextReadException = new InvalidDataException("Received 12 bytes: '32-02-00-00-00-00-00-00-00-00-81-04', expected 26 bytes.");
        var value = (await conn.ReadAsync(["MW0"])).ShouldHaveSingleItem();

        value.Quality.ShouldBe(QualityCode.BadAccessDenied);
        value.ErrorDetail!.ShouldContain("PUT/GET");
        conn.State.ShouldBe(ConnectionState.Connected);
    }

    [Fact]
    public async Task Connection_loss_marks_faulted_and_all_items_not_connected()
    {
        var (conn, plc) = Create();
        var states = new List<ConnectionState>();
        conn.StateChanged += (_, e) => states.Add(e.State);
        await conn.ConnectAsync();

        plc.NextReadException = new IOException("socket cerrado");
        var values = await conn.ReadAsync(["DB1.DBW0", "MW0"]);

        values.ShouldAllBe(v => v.Quality == QualityCode.BadNotConnected);
        conn.State.ShouldBe(ConnectionState.Faulted);
        states.ShouldBe([ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Faulted]);
        plc.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public async Task Writes_values_and_bits()
    {
        var (conn, plc) = Create();
        await conn.ConnectAsync();

        var results = await conn.WriteAsync([
            new WriteRequest("DB1.INT2", (short)-5),
            new WriteRequest("DB1.DBX0.7", true),
            new WriteRequest("MW0", 1234),
            new WriteRequest("DB1.STRING10[8]", "abc"),
        ]);

        results.ShouldAllBe(r => r.Success);
        var values = await conn.ReadAsync(["DB1.INT2", "DB1.DBX0.7", "MW0", "DB1.STRING10[8]"]);
        values.Select(v => v.Value).ShouldBe([(short)-5, true, (ushort)1234, "abc"]);
        plc.Memory[(S7Area.DataBlock, 1)][0].ShouldBe((byte)0x80);
    }

    [Fact]
    public async Task Write_with_wrong_value_reports_type_mismatch()
    {
        var (conn, _) = Create();
        await conn.ConnectAsync();
        var results = await conn.WriteAsync([new WriteRequest("DB1.INT0", 99999), new WriteRequest("DB1.REAL4", "abc")]);
        results.ShouldAllBe(r => r.Quality == QualityCode.BadTypeMismatch);
    }

    [Fact]
    public void ValidateAddress_reports_type_or_error()
    {
        var (conn, _) = Create();
        conn.ValidateAddress("DB1.REAL0").ShouldBe(AddressValidation.Valid(TagDataType.Float));
        conn.ValidateAddress("DB1.NOPE").IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Disconnect_and_reconnect()
    {
        var (conn, _) = Create();
        await conn.ConnectAsync();
        await conn.DisconnectAsync();
        conn.State.ShouldBe(ConnectionState.Disconnected);
        await conn.ConnectAsync();
        (await conn.ReadAsync(["DB1.DBB0"]))[0].Quality.ShouldBe(QualityCode.Good);
        (await conn.GetDeviceInfoAsync())["Cpu"].ShouldBe("Fake");
        await conn.DisposeAsync();
    }

    [Fact]
    public void Factory_validates_settings()
    {
        var factory = new S7AbsoluteDriverFactory();
        factory.TypeId.ShouldBe("s7-absolute");
        var errors = factory.ValidateSettings(JsonSerializer.SerializeToElement(new { host = "", port = 0, rack = 9, slot = 40, timeoutMs = 10 }));
        errors.Count.ShouldBe(5);
        factory.ValidateSettings(JsonSerializer.SerializeToElement(new { host = "192.168.0.10", cpu = "S7300", slot = 2 })).ShouldBeEmpty();
    }
}
