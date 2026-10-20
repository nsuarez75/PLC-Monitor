using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlcMonitor.Abstractions;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcMonitor.Drivers.S7Symbolic.Tests;

public class S7SymbolicConnectionTests
{
    /// <summary>CPU simulada: símbolos con su tipo y su valor actual.</summary>
    private sealed class FakeClient : ISymbolicClient
    {
        public Dictionary<string, (uint Type, object Value)> Symbols { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ulong> WriteErrors { get; } = new(StringComparer.Ordinal);

        /// <summary>Si no es null, ConnectAsync lanza este error.</summary>
        public S7SymbolicException? ConnectError { get; set; }

        /// <summary>Si no es null, la siguiente lectura lanza este error.</summary>
        public S7SymbolicException? NextReadError { get; set; }

        public string? NegotiatedSecurity { get; set; } = "TLS";

        public int ResolveCalls { get; private set; }

        public int BrowseCalls { get; private set; }

        public List<SymbolEntry> Arrays { get; } = [];

        public List<List<string>> ReadBatches { get; } = [];

        public bool Disposed { get; private set; }

        /// <summary>
        /// Como la librería real: la resolución de símbolos usa el programa leído al conectar (o al invalidar), así que un
        /// símbolo añadido después no se encuentra hasta <see cref="InvalidateMetadataAsync"/>.
        /// </summary>
        public bool CachesMetadata { get; set; }

        private HashSet<string>? _sessionSymbols;

        public int InvalidateCalls { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (ConnectError is { } error)
                return Task.FromException(error);
            _sessionSymbols = [.. Symbols.Keys];
            return Task.CompletedTask;
        }

        public Task InvalidateMetadataAsync(CancellationToken cancellationToken)
        {
            InvalidateCalls++;
            _sessionSymbols = [.. Symbols.Keys];
            return Task.CompletedTask;
        }

        public Task<PlcTag?> ResolveTagAsync(string symbol, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            if (symbol.Contains('?', StringComparison.Ordinal))
                throw new ArgumentException("Symbol syntax error");
            if (CachesMetadata && _sessionSymbols?.Contains(symbol) != true)
                return Task.FromResult<PlcTag?>(null);
            return Task.FromResult(Symbols.TryGetValue(symbol, out var s) ? PlcTags.TagFactory(symbol, new ItemAddress(), s.Type, s.Value is Array) : null);
        }

        public Task ReadTagsAsync(IReadOnlyList<PlcTag> tags, CancellationToken cancellationToken)
        {
            ReadBatches.Add(tags.Select(t => t.Name).ToList());
            if (NextReadError is { } error)
            {
                NextReadError = null;
                return Task.FromException(error);
            }
            foreach (var tag in tags)
            {
                PlcTagValueMapper.TrySetValue(tag, Symbols[tag.Name].Value, out _).ShouldBeTrue();
                tag.Quality = PlcTagQC.TAG_QUALITY_GOOD;
                tag.LastReadError = 0;
            }
            return Task.CompletedTask;
        }

        public Task WriteTagsAsync(IReadOnlyList<PlcTag> tags, CancellationToken cancellationToken)
        {
            foreach (var tag in tags)
            {
                var error = WriteErrors.GetValueOrDefault(tag.Name);
                tag.ProcessWriteResult(error);
                if (error == 0)
                    Symbols[tag.Name] = (tag.Datatype, PlcTagValueMapper.GetValue(tag).Value!);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SymbolEntry>> BrowseAsync(CancellationToken cancellationToken)
        {
            BrowseCalls++;
            return Task.FromResult<IReadOnlyList<SymbolEntry>>(
                Symbols.Select(s => new SymbolEntry(s.Key, s.Value.Type)).Concat(Arrays).ToArray());
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static (S7SymbolicConnection Conn, FakeClient Cpu) Create()
    {
        var cpu = new FakeClient
        {
            Symbols =
            {
                ["Motor.Speed"] = (Softdatatype.S7COMMP_SOFTDATATYPE_REAL, 12.5f),
                ["Motor.Running"] = (Softdatatype.S7COMMP_SOFTDATATYPE_BOOL, true),
                ["Motor.Name"] = (Softdatatype.S7COMMP_SOFTDATATYPE_STRING, "M1"),
                ["Line.Counter"] = (Softdatatype.S7COMMP_SOFTDATATYPE_DINT, 42),
            },
        };
        var conn = new S7SymbolicConnection("PLC1", new S7SymbolicSettings { Host = "sim", Password = "x" }, NullLogger.Instance, () => cpu);
        return (conn, cpu);
    }

    [Fact]
    public async Task Connect_failure_reports_readable_error_and_faults()
    {
        var (conn, cpu) = Create();
        cpu.ConnectError = new S7SymbolicException("Conexión", S7Consts.errCliInvalidPassword);

        var ex = await Should.ThrowAsync<S7SymbolicException>(() => conn.ConnectAsync());
        ex.Message.ShouldContain("Contraseña incorrecta");
        conn.State.ShouldBe(ConnectionState.Faulted);
        cpu.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Cpu_without_tls_in_tls_mode_suggests_auto_or_legacy()
    {
        var (conn, cpu) = Create();
        cpu.ConnectError = new S7SymbolicException("Conexión", S7Consts.errInitSslResponse);

        var ex = await Should.ThrowAsync<S7SymbolicException>(() => conn.ConnectAsync());
        ex.Message.ShouldContain("comunicación segura (TLS)");
        ex.Message.ShouldContain("legacy");
        conn.State.ShouldBe(ConnectionState.Faulted);
    }

    [Fact]
    public async Task Device_info_reports_negotiated_security()
    {
        var (conn, cpu) = Create();
        cpu.NegotiatedSecurity = "legacy (sin TLS)";
        await conn.ConnectAsync();

        var info = await conn.GetDeviceInfoAsync();

        info["Security"].ShouldBe("legacy (sin TLS)");
    }

    [Fact]
    public async Task Reads_symbols_with_types()
    {
        var (conn, _) = Create();
        await conn.ConnectAsync();

        var values = await conn.ReadAsync(["Motor.Speed", "Motor.Running", "Motor.Name", "Line.Counter"]);

        values.Select(v => (v.Value, v.DataType)).ShouldBe([
            (12.5f, TagDataType.Float), (true, TagDataType.Boolean), ("M1", TagDataType.String), (42, TagDataType.Int32)]);
        values.ShouldAllBe(v => v.Quality == QualityCode.Good);
    }

    [Fact]
    public async Task Unknown_or_malformed_symbols_are_bad_and_resolution_is_cached()
    {
        var (conn, cpu) = Create();
        await conn.ConnectAsync();

        var values = await conn.ReadAsync(["Motor.Speed", "No.Existe", "Mal?Formado", "\"Abierto.x"]);
        values[0].Quality.ShouldBe(QualityCode.Good);
        values[1].Quality.ShouldBe(QualityCode.BadAddressInvalid);
        values[1].ErrorDetail!.ShouldContain("no existe");
        values[2].ErrorDetail!.ShouldContain("Symbol syntax error");
        values[3].ErrorDetail!.ShouldContain("Comillas");

        var resolveCalls = cpu.ResolveCalls;
        await conn.ReadAsync(["Motor.Speed", "No.Existe"]);
        cpu.ResolveCalls.ShouldBe(resolveCalls);
    }

    [Fact]
    public async Task A_symbol_added_to_the_plc_after_connecting_is_found()
    {
        var (conn, cpu) = Create();
        cpu.CachesMetadata = true;
        await conn.ConnectAsync();
        cpu.Symbols["Motor.Nueva"] = (Softdatatype.S7COMMP_SOFTDATATYPE_INT, (short)7);

        var value = (await conn.ReadAsync(["Motor.Nueva"])).Single();

        value.Quality.ShouldBe(QualityCode.Good);
        value.Value.ShouldBe((short)7);
        cpu.InvalidateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_symbols_do_not_reload_the_program_every_time()
    {
        var (conn, cpu) = Create();
        cpu.CachesMetadata = true;
        await conn.ConnectAsync();

        await conn.ReadAsync(["No.Existe"]);
        await conn.ReadAsync(["Tampoco.Existe"]);

        cpu.InvalidateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Rereading_the_browser_finds_symbols_that_were_missing()
    {
        var (conn, cpu) = Create();
        cpu.CachesMetadata = true;
        await conn.ConnectAsync();
        (await conn.ReadAsync(["Motor.Nueva"])).Single().Quality.ShouldBe(QualityCode.BadAddressInvalid);
        cpu.Symbols["Motor.Nueva"] = (Softdatatype.S7COMMP_SOFTDATATYPE_INT, (short)7);
        (await conn.ReadAsync(["Motor.Nueva"])).Single().Quality.ShouldBe(QualityCode.BadAddressInvalid);

        (await conn.BrowseAsync(new BrowseQuery("Nueva", Refresh: true))).Items.ShouldHaveSingleItem().Address.ShouldBe("Motor.Nueva");

        (await conn.ReadAsync(["Motor.Nueva"])).Single().Value.ShouldBe((short)7);
    }

    [Fact]
    public async Task Duplicate_addresses_are_read_once()
    {
        var (conn, cpu) = Create();
        await conn.ConnectAsync();
        var values = await conn.ReadAsync(["Line.Counter", "Line.Counter"]);
        values.ShouldAllBe(v => Equals(v.Value, 42));
        cpu.ReadBatches.ShouldHaveSingleItem().ShouldBe(["Line.Counter"]);
    }

    [Fact]
    public async Task Communication_error_faults_connection()
    {
        var (conn, cpu) = Create();
        await conn.ConnectAsync();
        cpu.NextReadError = new S7SymbolicException("Lectura", S7Consts.errTCPDataReceive);

        var values = await conn.ReadAsync(["Motor.Speed"]);

        values[0].Quality.ShouldBe(QualityCode.BadNotConnected);
        conn.State.ShouldBe(ConnectionState.Faulted);
    }

    [Fact]
    public async Task Non_communication_error_keeps_connection()
    {
        var (conn, cpu) = Create();
        await conn.ConnectAsync();
        cpu.NextReadError = new S7SymbolicException("Lectura", S7Consts.errCliItemNotAvailable);

        var values = await conn.ReadAsync(["Motor.Speed"]);

        values[0].Quality.ShouldBe(QualityCode.Bad);
        conn.State.ShouldBe(ConnectionState.Connected);
    }

    [Fact]
    public async Task Writes_convert_values_and_report_cpu_rejections()
    {
        var (conn, cpu) = Create();
        cpu.WriteErrors["Motor.Running"] = 0x8203;
        await conn.ConnectAsync();

        var results = await conn.WriteAsync([
            new WriteRequest("Motor.Speed", "33.5"),
            new WriteRequest("Line.Counter", 1.0e12),
            new WriteRequest("Motor.Running", false),
            new WriteRequest("No.Existe", 1),
        ]);

        results.Select(r => r.Quality).ShouldBe([
            QualityCode.Good, QualityCode.BadTypeMismatch, QualityCode.BadAccessDenied, QualityCode.BadAddressInvalid]);
        cpu.Symbols["Motor.Speed"].Value.ShouldBe(33.5f);
        (await conn.ReadAsync(["Motor.Speed"]))[0].Value.ShouldBe(33.5f);
    }

    [Fact]
    public async Task Browse_filters_limits_and_caches()
    {
        var (conn, cpu) = Create();
        await conn.ConnectAsync();

        var result = await conn.BrowseAsync(new BrowseQuery("motor", MaxResults: 2));
        result.TotalSymbols.ShouldBe(4);
        result.TotalMatches.ShouldBe(3);
        result.Items.Count.ShouldBe(2);
        result.Items[0].NativeType.ShouldBe("Real");

        await conn.BrowseAsync(new BrowseQuery());
        cpu.BrowseCalls.ShouldBe(1);
        await conn.BrowseAsync(new BrowseQuery(Refresh: true));
        cpu.BrowseCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Browse_offers_arrays_whole_and_by_element()
    {
        var (conn, cpu) = Create();
        cpu.Arrays.Add(new SymbolEntry("Arrays.Strings", Softdatatype.S7COMMP_SOFTDATATYPE_STRING, [new ArrayDimension(0, 3)]));
        cpu.Arrays.Add(new SymbolEntry("Arrays.Matrix", Softdatatype.S7COMMP_SOFTDATATYPE_INT, [new ArrayDimension(1, 2), new ArrayDimension(0, 2)]));
        await conn.ConnectAsync();

        var strings = await conn.BrowseAsync(new BrowseQuery("Arrays.Strings"));
        strings.Items.Select(i => (i.Address, i.NativeType)).ShouldBe([
            ("Arrays.Strings", "Array[0..2] of String"),
            ("Arrays.Strings[0]", "String"),
            ("Arrays.Strings[1]", "String"),
            ("Arrays.Strings[2]", "String")]);
        strings.Items.ShouldAllBe(i => i.DataType == TagDataType.String);
        strings.TotalSymbols.ShouldBe(4 + 4 + 5);

        var matrix = await conn.BrowseAsync(new BrowseQuery("Matrix"));
        matrix.Items.Select(i => i.Address).ShouldBe([
            "Arrays.Matrix", "Arrays.Matrix[1,0]", "Arrays.Matrix[1,1]", "Arrays.Matrix[2,0]", "Arrays.Matrix[2,1]"]);
        matrix.Items[0].NativeType.ShouldBe("Array[1..2, 0..1] of Int");
    }

    [Fact]
    public async Task Browse_requires_connection()
    {
        var (conn, _) = Create();
        await Should.ThrowAsync<InvalidOperationException>(() => conn.BrowseAsync(new BrowseQuery()));
    }

    [Theory]
    [InlineData("\"DB\".a.b", true)]
    [InlineData("DB.arr[3].x", true)]
    [InlineData("", false)]
    [InlineData("a..b", false)]
    [InlineData(".a", false)]
    [InlineData("a[1", false)]
    public void Validates_symbol_syntax(string address, bool valid)
    {
        var (conn, _) = Create();
        conn.ValidateAddress(address).IsValid.ShouldBe(valid);
    }

    [Fact]
    public void Factory_validates_settings()
    {
        var factory = new S7SymbolicDriverFactory();
        factory.TypeId.ShouldBe("s7-symbolic");
        factory.Capabilities.HasFlag(DriverCapabilities.Browse).ShouldBeTrue();
        factory.ValidateSettings(JsonSerializer.SerializeToElement(new { host = "", timeoutMs = 10, username = "u" })).Count.ShouldBe(3);
        factory.ValidateSettings(JsonSerializer.SerializeToElement(new { host = "192.168.2.1", password = "p" })).ShouldBeEmpty();
        factory.ValidateSettings(JsonSerializer.SerializeToElement(new { host = "10.0.0.1", security = "legacy" })).ShouldBeEmpty();
    }

    [Fact]
    public void Communication_codes_are_classified()
    {
        S7SymbolicException.IsCommunicationCode(S7Consts.errTCPConnectionReset).ShouldBeTrue();
        S7SymbolicException.IsCommunicationCode(S7Consts.errTCPUnreachableHost).ShouldBeTrue();
        S7SymbolicException.IsCommunicationCode(S7Consts.errIsoInvalidPDU).ShouldBeTrue();
        S7SymbolicException.IsCommunicationCode(S7Consts.errCliJobTimeout).ShouldBeTrue();
        S7SymbolicException.IsCommunicationCode(S7Consts.errOpenSSL).ShouldBeTrue();
        S7SymbolicException.IsCommunicationCode(S7Consts.errS7CommPlusDigestMismatch).ShouldBeTrue();
        S7SymbolicException.IsCommunicationCode(S7Consts.errCliInvalidPassword).ShouldBeFalse();
        S7SymbolicException.IsCommunicationCode(S7Consts.errCliItemNotAvailable).ShouldBeFalse();
        S7SymbolicException.IsCommunicationCode(S7Consts.errS7CommPlusLegacyAuthentication).ShouldBeFalse();
        new S7SymbolicException("Lectura", S7Consts.errCliFunctionRefused, isCommunicationError: true).IsCommunicationError.ShouldBeTrue();
    }
}
