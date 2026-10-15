using Microsoft.Extensions.Logging.Abstractions;
using PlcMonitor.Abstractions;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;
using PlcMonitor.Engine.Tests;
using static PlcMonitor.Engine.Tests.EngineHarness;

namespace PlcMonitor.Historian.Tests;

/// <summary>Historiador completo sobre el motor con driver simulado y reloj controlado.</summary>
public sealed class HistorianServiceTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("plcmon-hist-svc-").FullName;
    private readonly List<IAsyncDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TagConfiguration Historized(string address, string name, int minMs = 0, int maxMs = 0, double deadband = 0) => new()
    {
        Address = address,
        Name = name,
        History = new TagHistorySettings { Enabled = true, MinIntervalMs = minMs, MaxIntervalMs = maxMs, Deadband = deadband },
    };

    private async Task<(EngineHarness Harness, HistorianService Historian)> StartAsync(params DeviceConfiguration[] devices)
    {
        var h = new EngineHarness(devices);
        var historian = new HistorianService(h.Engine, new HistorianSettings { Path = "history", FlushIntervalMs = 200, RetentionDays = 90 }, _dir,
            NullLogger<HistorianService>.Instance, h.Time);
        _owned.Add(historian);
        _owned.Add(h);
        await h.Engine.StartAsync();
        await historian.StartAsync();
        return (h, historian);
    }

    // El driver simulado marca los valores con la hora real y el motor usa un reloj simulado: el rango cubre ambos.
    private static Task<IReadOnlyList<HistorySeries>> Query(HistorianService historian, EngineHarness h, string tag, TimeSpan? interval = null) =>
        historian.QueryAsync(new HistoryQuery([new TagKey("plc1", tag)], h.Time.GetUtcNow().AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), interval), Ct);

    [Fact]
    public async Task Only_historized_tags_are_stored_and_can_be_queried()
    {
        var (h, historian) = await StartAsync(Device("plc1", intervalMs: 100, tags: [Historized("DB1.DBW0", "velocidad"), Tag("DB1.DBW2", "sin_historico")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 10;
        plc.Values["DB1.DBW2"] = 5;
        await h.AdvanceUntilAsync(() => h.Value("plc1", "velocidad").Quality == QualityCode.Good);

        plc.Values["DB1.DBW0"] = 20;
        await h.AdvanceUntilAsync(() => Equals(h.Value("plc1", "velocidad").Value, 20));
        plc.Values["DB1.DBW0"] = 30;
        await h.AdvanceUntilAsync(() => Equals(h.Value("plc1", "velocidad").Value, 30));

        var series = (await Query(historian, h, "velocidad")).ShouldHaveSingleItem();
        series.Points.Where(p => p.Quality == QualityCode.Good).Select(p => p.Number).ShouldBe([10.0, 20.0, 30.0]);
        series.Points[0].Quality.ShouldBe(QualityCode.BadNotConnected); // estado inicial antes de conectar
        (await Query(historian, h, "sin_historico")).ShouldHaveSingleItem().Points.ShouldBeEmpty();

        var stats = historian.GetStats();
        stats.HistorizedTags.ShouldBe(1);
        stats.Files.ShouldBe(1);
        stats.Path.ShouldBe(Path.Combine(_dir, "history"));

        var buckets = (await Query(historian, h, "velocidad", TimeSpan.FromDays(10))).Single().Points;
        buckets.ShouldHaveSingleItem().Number.ShouldBe(20.0);
    }

    [Fact]
    public async Task Maximum_interval_writes_even_without_changes()
    {
        var (h, historian) = await StartAsync(Device("plc1", intervalMs: 100, tags: [Historized("DB1.DBW0", "t", maxMs: 1000)]));
        h.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        await h.AdvanceUntilAsync(() => h.Value("plc1", "t").Quality == QualityCode.Good);

        var start = h.Time.GetUtcNow();
        await h.AdvanceUntilAsync(() => h.Time.GetUtcNow() - start >= TimeSpan.FromSeconds(3.5), stepMs: 100);

        var good = (await Query(historian, h, "t")).Single().Points.Where(p => p.Quality == QualityCode.Good).ToArray();
        // El primero y un latido por segundo. Con el reloj simulado avanzando a saltos y la máquina cargada se pueden
        // juntar pasos del reloj, así que se exige que haya latidos, no un número exacto.
        good.Length.ShouldBeInRange(3, 5);
        good.ShouldAllBe(p => p.Number == 1.0);
    }

    [Fact]
    public async Task Configuration_changes_start_and_stop_historizing()
    {
        var (h, historian) = await StartAsync(Device("plc1", intervalMs: 100, tags: [Tag("DB1.DBW0", "t")]));
        var plc = h.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        await h.AdvanceUntilAsync(() => h.Value("plc1", "t").Quality == QualityCode.Good);
        historian.GetStats().HistorizedTags.ShouldBe(0);

        await h.Engine.PutDeviceAsync(Device("plc1", intervalMs: 100, tags: [Historized("DB1.DBW0", "t")]));
        await h.AdvanceUntilAsync(() => historian.GetStats().HistorizedTags == 1 && h.Value("plc1", "t").Quality == QualityCode.Good);
        plc.Values["DB1.DBW0"] = 2;
        await h.AdvanceUntilAsync(() => Equals(h.Value("plc1", "t").Value, 2));

        (await Query(historian, h, "t")).Single().Points.Where(p => p.Quality == QualityCode.Good).Select(p => p.Number).ShouldContain(2.0);

        await h.Engine.DeleteDeviceAsync("plc1");
        await h.AdvanceUntilAsync(() => historian.GetStats().HistorizedTags == 0);
    }

    [Fact]
    public async Task Disabled_historian_refuses_queries()
    {
        var h = new EngineHarness(Device("plc1"));
        _owned.Add(h);
        await using var historian = new HistorianService(h.Engine, new HistorianSettings { Enabled = false }, _dir, NullLogger<HistorianService>.Instance, h.Time);
        await historian.StartAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => historian.QueryAsync(new HistoryQuery([], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), Ct));
        historian.GetStats().Enabled.ShouldBeFalse();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned)
            await owned.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }
}
