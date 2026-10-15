using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Drivers.S7Absolute;
using PlcMonitor.Drivers.S7Symbolic;

namespace PlcMonitor.IntegrationTests;

/// <summary>Pruebas de solo lectura contra PLCs reales (ver variables de entorno en el .csproj).</summary>
public class RealPlcTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static async Task<IDeviceConnection> ConnectSymbolicAsync()
    {
        var host = Env("PLCMON_IT_SYMBOLIC_HOST");
        Assert.SkipWhen(host is null, "PLCMON_IT_SYMBOLIC_HOST no definida");
        var settings = new S7SymbolicSettings
        {
            Host = host!,
            Password = Env("PLCMON_IT_PASSWORD") ?? string.Empty,
            Security = Enum.Parse<S7SymbolicSecurity>(Env("PLCMON_IT_SECURITY") ?? "auto", ignoreCase: true),
        };
        var device = new DeviceDefinition("it-symbolic", S7SymbolicDriverFactory.Id, JsonSerializer.SerializeToElement(settings, DriverJson.Options));
        var conn = new S7SymbolicDriverFactory().CreateConnection(device, NullLoggerFactory.Instance);
        await conn.ConnectAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    [Fact]
    public async Task Symbolic_connects_with_expected_security()
    {
        await using var conn = await ConnectSymbolicAsync();
        conn.State.ShouldBe(ConnectionState.Connected);
        // PLCMON_IT_EXPECTED_SECURITY: "TLS" o "legacy" (opcional).
        if (Env("PLCMON_IT_EXPECTED_SECURITY") is { } expected)
            (await conn.GetDeviceInfoAsync(TestContext.Current.CancellationToken))["Security"].ShouldStartWith(expected, Case.Insensitive);
    }

    [Fact]
    public async Task Symbolic_browse_finds_symbols()
    {
        await using var conn = await ConnectSymbolicAsync();
        var result = await ((IBrowsableConnection)conn).BrowseAsync(new BrowseQuery(MaxResults: 10), TestContext.Current.CancellationToken);
        result.TotalSymbols.ShouldBeGreaterThan(0);
        result.Items.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Symbolic_reads_symbol_and_rejects_unknown()
    {
        var symbol = Env("PLCMON_IT_SYMBOL");
        Assert.SkipWhen(symbol is null, "PLCMON_IT_SYMBOL no definida");
        await using var conn = await ConnectSymbolicAsync();

        var values = await conn.ReadAsync([symbol!, "PlcMonitor_NoExiste.X"], TestContext.Current.CancellationToken);

        values[0].Quality.ShouldBe(QualityCode.Good, values[0].ErrorDetail);
        values[0].DataType.ShouldNotBe(TagDataType.Unknown);
        values[1].Quality.ShouldBe(QualityCode.BadAddressInvalid);
    }

    [Fact]
    public async Task Symbolic_poller_emits_initial_values()
    {
        var symbol = Env("PLCMON_IT_SYMBOL");
        Assert.SkipWhen(symbol is null, "PLCMON_IT_SYMBOL no definida");
        await using var conn = await ConnectSymbolicAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await foreach (var changes in TagPoller.WatchAsync(conn, [symbol!], TimeSpan.FromMilliseconds(250), cancellationToken: cts.Token))
        {
            changes.ShouldHaveSingleItem().Value.Quality.ShouldBe(QualityCode.Good);
            break;
        }
    }

    [Fact]
    public async Task Wrong_password_is_reported()
    {
        var host = Env("PLCMON_IT_SYMBOLIC_HOST");
        Assert.SkipWhen(host is null || Env("PLCMON_IT_PASSWORD") is null, "Requiere PLC con contraseña");
        var settings = new S7SymbolicSettings { Host = host!, Password = "PlcMonitor-contraseña-incorrecta" };
        var device = new DeviceDefinition("it-bad", S7SymbolicDriverFactory.Id, JsonSerializer.SerializeToElement(settings, DriverJson.Options));
        await using var conn = new S7SymbolicDriverFactory().CreateConnection(device, NullLoggerFactory.Instance);
        await Should.ThrowAsync<S7SymbolicException>(() => conn.ConnectAsync(TestContext.Current.CancellationToken));
        conn.State.ShouldBe(ConnectionState.Faulted);
    }

    [Fact]
    public async Task Absolute_reads_address()
    {
        var host = Env("PLCMON_IT_ABSOLUTE_HOST");
        var address = Env("PLCMON_IT_ABSOLUTE_ADDR");
        Assert.SkipWhen(host is null || address is null, "PLCMON_IT_ABSOLUTE_HOST / PLCMON_IT_ABSOLUTE_ADDR no definidas");
        var settings = new S7AbsoluteSettings { Host = host! };
        var device = new DeviceDefinition("it-absolute", S7AbsoluteDriverFactory.Id, JsonSerializer.SerializeToElement(settings, DriverJson.Options));
        await using var conn = new S7AbsoluteDriverFactory().CreateConnection(device, NullLoggerFactory.Instance);
        await conn.ConnectAsync(TestContext.Current.CancellationToken);
        (await conn.ReadAsync([address!], TestContext.Current.CancellationToken))[0].Quality.ShouldBe(QualityCode.Good);
    }
}
