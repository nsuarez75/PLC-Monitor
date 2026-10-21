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
    public async Task Renaming_moves_the_history_in_every_month_and_joins_existing_samples()
    {
        using var store = new SqliteHistorianStore(_dir);
        var march = new DateTimeOffset(2026, 3, 31, 23, 0, 0, TimeSpan.Zero);
        var april = march.AddHours(2);
        var renamed = new TagKey("plc1", "rapidez");
        await store.WriteAsync([S(Speed, march, 1), S(Speed, april, 2), S(Name, april, "a")], Ct);
        // Lo guardado con el nombre nuevo después del cambio (antes de mover el histórico).
        await store.WriteAsync([S(renamed, april, 2), S(renamed, april.AddMinutes(5), 3)], Ct);

        var moved = await store.RenameAsync([new HistoryRename("plc1", "velocidad", "plc1", "rapidez")], Ct);

        moved.ShouldBe(1);
        (await store.ReadAsync(renamed, march.AddDays(-1), april.AddDays(1), 100, Ct)).Select(p => p.Number).ShouldBe([1.0, 2.0, 3.0]);
        (await store.ReadAsync(Speed, march.AddDays(-1), april.AddDays(1), 100, Ct)).ShouldBeEmpty();
        // Lo nuevo con el nombre antiguo empieza de cero.
        await store.WriteAsync([S(Speed, april.AddMinutes(10), 9)], Ct);
        (await store.ReadAsync(Speed, march.AddDays(-1), april.AddDays(1), 100, Ct)).Select(p => p.Number).ShouldBe([9.0]);

        var device = await store.RenameAsync([new HistoryRename("plc1", null, "prensa", null)], Ct);

        device.ShouldBe(3);
        (await store.ReadAsync(new TagKey("prensa", "rapidez"), march.AddDays(-1), april.AddDays(1), 100, Ct)).Count.ShouldBe(3);
        (await store.ReadAsync(new TagKey("prensa", "nombre"), march.AddDays(-1), april.AddDays(1), 100, Ct)).Single().Text.ShouldBe("a");
        (await store.ReadAsync(renamed, march.AddDays(-1), april.AddDays(1), 100, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_removes_only_the_chosen_tags_between_both_instants_across_months()
    {
        using var store = new SqliteHistorianStore(_dir);
        var march = new DateTimeOffset(2026, 3, 31, 22, 0, 0, TimeSpan.Zero);
        await store.WriteAsync([.. Enumerable.Range(0, 5).Select(h => S(Speed, march.AddHours(h), h)), S(Name, march.AddHours(1), "a")], Ct);

        // De 23:00 del 31 de marzo a 01:00 del 1 de abril, ambos incluidos.
        var deleted = await store.DeleteAsync([Speed, new TagKey("plc1", "sin datos")], march.AddHours(1), march.AddHours(3), Ct);

        deleted.ShouldBe(3);
        (await store.ReadAsync(Speed, march.AddDays(-1), march.AddDays(1), 100, Ct)).Select(p => p.Number).ShouldBe([0.0, 4.0]);
        (await store.ReadAsync(Name, march.AddDays(-1), march.AddDays(1), 100, Ct)).ShouldHaveSingleItem();
        (await store.DeleteAsync([Speed], march.AddYears(-1), march.AddYears(-1).AddDays(1), Ct)).ShouldBe(0);
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
