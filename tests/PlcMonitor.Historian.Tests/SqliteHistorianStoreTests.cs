using PlcMonitor.Abstractions;
using PlcMonitor.Engine;

namespace PlcMonitor.Historian.Tests;

public sealed class SqliteHistorianStoreTests : IDisposable
{
    private static readonly TagKey Speed = new("plc1", "velocidad");
    private static readonly TagKey Name = new("plc1", "nombre");
    private readonly string _dir = Directory.CreateTempSubdirectory("plcmon-hist-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HistorySample S(TagKey key, DateTimeOffset at, object? value, QualityCode quality = QualityCode.Good) =>
        new(key, at, value, TagDataType.Double, quality);

    [Fact]
    public async Task Samples_are_read_back_across_months()
    {
        using var store = new SqliteHistorianStore(_dir);
        var sep = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);
        var oct = new DateTimeOffset(2026, 10, 1, 0, 1, 0, TimeSpan.Zero);
        await store.WriteAsync([S(Speed, oct, 2.5), S(Speed, sep, 1.5), S(Name, oct, "M1"), S(Speed, oct.AddMinutes(1), true)], Ct);

        var speed = await store.ReadAsync(Speed, sep.AddHours(-1), oct.AddHours(1), 100, Ct);
        var name = await store.ReadAsync(Name, sep, oct.AddHours(1), 100, Ct);

        speed.Select(p => (p.Timestamp, p.Number)).ShouldBe([(sep, 1.5), (oct, 2.5), (oct.AddMinutes(1), 1.0)]);
        name.ShouldHaveSingleItem().Text.ShouldBe("M1");
        name[0].Number.ShouldBeNull();
        Directory.GetFiles(_dir, "history-*.db").Select(Path.GetFileName).Order().ShouldBe(["history-2026-09.db", "history-2026-10.db"]);
        (await store.ReadAsync(new TagKey("plc1", "otro"), sep, oct, 10, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Bad_quality_samples_keep_their_quality()
    {
        using var store = new SqliteHistorianStore(_dir);
        var t = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        await store.WriteAsync([S(Speed, t, null, QualityCode.BadNotConnected)], Ct);
        var point = (await store.ReadAsync(Speed, t, t, 10, Ct)).ShouldHaveSingleItem();
        (point.Number, point.Text, point.Quality).ShouldBe((null, null, QualityCode.BadNotConnected));
    }

    [Fact]
    public async Task Reads_stop_after_max_points_plus_one()
    {
        using var store = new SqliteHistorianStore(_dir);
        var t = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        await store.WriteAsync(Enumerable.Range(0, 50).Select(i => S(Speed, t.AddSeconds(i), (double)i)).ToArray(), Ct);
        (await store.ReadAsync(Speed, t, t.AddHours(1), 10, Ct)).Count.ShouldBe(11);
    }

    [Fact]
    public async Task Purge_removes_old_months_and_old_rows()
    {
        using var store = new SqliteHistorianStore(_dir);
        var aug = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var oct = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        await store.WriteAsync([S(Speed, aug, 1.0), S(Speed, oct, 2.0), S(Speed, oct.AddDays(10), 3.0)], Ct);

        await store.PurgeAsync(oct.AddDays(5), Ct);

        File.Exists(Path.Combine(_dir, "history-2026-08.db")).ShouldBeFalse();
        (await store.ReadAsync(Speed, aug, oct.AddMonths(1), 10, Ct)).Select(p => p.Number).ShouldBe([3.0]);
        store.Usage().Files.ShouldBe(1);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
