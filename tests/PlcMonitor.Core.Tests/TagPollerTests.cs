using Microsoft.Extensions.Time.Testing;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Core.Tests;

public class TagPollerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task First_poll_emits_all_values()
    {
        var conn = new FakeDeviceConnection { Values = { ["A"] = 1, ["B"] = 2 } };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var e = TagPoller.WatchAsync(conn, ["A", "B"], Interval, new FakeTimeProvider(), cts.Token).GetAsyncEnumerator(cts.Token);

        (await e.MoveNextAsync()).ShouldBeTrue();
        e.Current.Select(c => (c.Address, c.Value.Value)).ShouldBe([("A", (object?)1), ("B", 2)]);
    }

    [Fact]
    public async Task Later_polls_emit_only_changes()
    {
        var conn = new FakeDeviceConnection { Values = { ["A"] = 1, ["B"] = 2 } };
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = TagPoller.WatchAsync(conn, ["A", "B"], Interval, time, cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).ShouldBeTrue();

        conn.Values["B"] = 20;
        var next = e.MoveNextAsync().AsTask();
        time.Advance(Interval);

        (await next).ShouldBeTrue();
        e.Current.ShouldHaveSingleItem().Address.ShouldBe("B");
        e.Current[0].Value.Value.ShouldBe(20);
    }

    [Fact]
    public async Task Unchanged_values_produce_no_emission()
    {
        var conn = new FakeDeviceConnection { Values = { ["A"] = 1 } };
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = TagPoller.WatchAsync(conn, ["A"], Interval, time, cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).ShouldBeTrue();

        var next = e.MoveNextAsync().AsTask();
        time.Advance(Interval);
        time.Advance(Interval);
        await WaitUntil(() => conn.ReadCount >= 3);
        next.IsCompleted.ShouldBeFalse();

        conn.Values["A"] = 2;
        time.Advance(Interval);
        (await next).ShouldBeTrue();
        e.Current.ShouldHaveSingleItem().Value.Value.ShouldBe(2);
    }

    [Fact]
    public async Task Read_failure_marks_all_tags_bad_and_keeps_polling()
    {
        var conn = new FakeDeviceConnection { Values = { ["A"] = 1 } };
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = TagPoller.WatchAsync(conn, ["A"], Interval, time, cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).ShouldBeTrue();

        conn.ReadException = new IOException("cable desconectado");
        conn.State = ConnectionState.Faulted;
        var next = e.MoveNextAsync().AsTask();
        time.Advance(Interval);
        (await next).ShouldBeTrue();
        e.Current[0].Value.Quality.ShouldBe(QualityCode.BadNotConnected);
        e.Current[0].Value.ErrorDetail.ShouldBe("cable desconectado");

        conn.ReadException = null;
        conn.State = ConnectionState.Connected;
        next = e.MoveNextAsync().AsTask();
        time.Advance(Interval);
        (await next).ShouldBeTrue();
        e.Current[0].Value.Quality.ShouldBe(QualityCode.Good);
    }

    [Fact]
    public void Detects_changes_in_byte_arrays_by_content()
    {
        var last = new TagValue?[1];
        var t = DateTimeOffset.UnixEpoch;
        TagPoller.DetectChanges(["A"], last, [TagValue.Good(new byte[] { 1, 2 }, TagDataType.ByteArray, t)]).Count.ShouldBe(1);
        TagPoller.DetectChanges(["A"], last, [TagValue.Good(new byte[] { 1, 2 }, TagDataType.ByteArray, t.AddSeconds(1))]).Count.ShouldBe(0);
        TagPoller.DetectChanges(["A"], last, [TagValue.Good(new byte[] { 1, 3 }, TagDataType.ByteArray, t)]).Count.ShouldBe(1);
    }

    [Fact]
    public void Rejects_invalid_interval() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            TagPoller.WatchAsync(new FakeDeviceConnection(), ["A"], TimeSpan.Zero).GetAsyncEnumerator().MoveNextAsync().AsTask().GetAwaiter().GetResult());

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        condition().ShouldBeTrue();
    }
}
