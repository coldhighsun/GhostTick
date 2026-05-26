using System.Diagnostics;
using System.Threading.Channels;

namespace GhostTick.Tests;

public class GhostTickerTests
{
    [Fact]
    public async Task Custom_options_respected()
    {
        var opts = new GhostTickerOptions
        {
            SpinThreshold = TimeSpan.FromMilliseconds(2),
            ChannelCapacity = 4,
            FullMode = BoundedChannelFullMode.DropOldest,
            ThreadPriority = ThreadPriority.Normal,
            ThreadName = "MyTicker",
        };
        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(30), opts);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var evt = await ticker.Reader.ReadAsync(cts.Token);
        Assert.Equal(1UL, evt.Sequence);
    }

    [Fact]
    public async Task Dispose_completes_channel()
    {
        var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));
        await ticker.Reader.ReadAsync(TestContext.Current.CancellationToken);
        ticker.Dispose();

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await ticker.Reader.WaitToReadAsync(cts.Token))
                ticker.Reader.TryRead(out _);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Channel did not complete after Dispose().");
        }
    }

    [Fact]
    public void Dispose_does_not_throw()
    {
        var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));
        ticker.Dispose();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));
        ticker.Dispose();
        ticker.Dispose(); // must not throw
    }

    [Fact]
    public async Task FiredAt_is_not_before_ScheduledAt()
    {
        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(20));
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        for (var i = 0; i < 5; i++)
        {
            var evt = await ticker.Reader.ReadAsync(cts.Token);
            Assert.True(evt.FiredAt >= evt.ScheduledAt,
                $"Tick {evt.Sequence}: FiredAt {evt.FiredAt:O} is before ScheduledAt {evt.ScheduledAt:O}");
        }
    }

    [Fact]
    public void Negative_interval_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GhostTicker(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task Stop_completes_channel()
    {
        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));

        // Consume a couple of ticks, then stop.
        await ticker.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await ticker.Reader.ReadAsync(TestContext.Current.CancellationToken);
        ticker.Stop();

        // Drain until complete; should not hang.
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await ticker.Reader.WaitToReadAsync(cts.Token))
                ticker.Reader.TryRead(out _);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Channel did not complete after Stop().");
        }
    }

    [Fact]
    public async Task Ticker_drift_corrects_over_time()
    {
        // Compare first-tick scheduled vs last-tick scheduled; gap should be (n-1)*interval.
        var interval = TimeSpan.FromMilliseconds(20);
        using var ticker = new GhostTicker(interval);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        TimerEvent first = default, last = default;
        for (var i = 0; i < 20; i++)
        {
            var evt = await ticker.Reader.ReadAsync(cts.Token);
            if (i == 0)
                first = evt;
            last = evt;
        }

        var gap = last.ScheduledAt - first.ScheduledAt;
        var expected = interval * 19; // from tick 1 to tick 20 = 19 intervals
        Assert.InRange(gap, expected - TimeSpan.FromMicroseconds(100), expected + TimeSpan.FromMicroseconds(100));
    }

    [Fact]
    public async Task Ticker_emits_sequential_events()
    {
        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(20));
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var events = new List<TimerEvent>();
        while (await ticker.Reader.WaitToReadAsync(cts.Token))
        {
            if (ticker.Reader.TryRead(out var evt))
                events.Add(evt);

            if (events.Count >= 5)
                break;
        }

        Assert.Equal(5, events.Count);
        for (var i = 0; i < events.Count; i++)
            Assert.Equal((ulong)(i + 1), events[i].Sequence);
    }

    [Fact]
    public async Task Ticker_interval_is_accurate()
    {
        var interval = TimeSpan.FromMilliseconds(50);
        using var ticker = new GhostTicker(interval);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Collect 10 ticks and measure total elapsed time.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
            await ticker.Reader.ReadAsync(cts.Token);
        sw.Stop();

        var expected = interval * 10;
        // Allow ±15 ms total for 10 ticks.
        Assert.InRange(sw.Elapsed, expected - TimeSpan.FromMilliseconds(5), expected + TimeSpan.FromMilliseconds(15));
    }

    [Fact]
    public void Zero_interval_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GhostTicker(TimeSpan.Zero));
    }
}