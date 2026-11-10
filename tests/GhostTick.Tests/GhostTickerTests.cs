using System.Diagnostics;
using System.Threading.Channels;

namespace GhostTick.Tests;

public class GhostTickerTests
{
    [Fact]
    public async Task Constructor_CustomOptions_AreRespected()
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

        // Let several ticks pile up without reading: only a capacity > 1 can buffer them.
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        var sequences = new List<ulong>();
        while (ticker.Reader.TryRead(out var evt))
        {
            sequences.Add(evt.Sequence);
        }

        // Ticks are due at ~30/60/90 ms, so at least two are buffered even with some scheduling jitter.
        // The first one is not asserted: a long pause could push older ticks out of the capacity-4 buffer.
        Assert.True(sequences.Count >= 2, $"Expected at least 2 buffered ticks, got {sequences.Count}.");
        for (var i = 1; i < sequences.Count; i++)
        {
            Assert.Equal(sequences[i - 1] + 1, sequences[i]);
        }
    }

    [Fact]
    public async Task Dispose_Called_CompletesChannel()
    {
        var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));
        await ticker.Reader.ReadAsync(TestContext.Current.CancellationToken);
        ticker.Dispose();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
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
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));
        ticker.Dispose();
        ticker.Dispose(); // must not throw
    }

    [Fact]
    public async Task Reader_EachTick_FiredAtNotBeforeScheduledAt()
    {
        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(20));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        for (var i = 0; i < 5; i++)
        {
            var evt = await ticker.Reader.ReadAsync(cts.Token);
            Assert.True(evt.FiredAt >= evt.ScheduledAt,
                $"Tick {evt.Sequence}: FiredAt {evt.FiredAt:O} is before ScheduledAt {evt.ScheduledAt:O}");
        }
    }

    [Fact]
    public void Constructor_NegativeInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GhostTicker(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task Stop_Called_CompletesChannel()
    {
        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));

        // Consume a couple of ticks, then stop.
        await ticker.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await ticker.Reader.ReadAsync(TestContext.Current.CancellationToken);
        ticker.Stop();

        // Drain until complete; should not hang.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
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

    /// <summary>
    /// Verifies that drift correction keeps late ticks from accumulating: the median drift of the second half of the run
    /// must not exceed that of the first half by more than scheduling noise.
    /// </summary>
    [Fact]
    public async Task Reader_LongRun_DriftDoesNotGrow()
    {
        // With drift correction, late ticks do not push later ticks back, so the drift in the
        // second half of the run must not exceed the first half by more than scheduling noise.
        // The comparison is relative (not an absolute threshold) so it holds on loaded CI machines.
        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(20));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var drifts = new List<TimeSpan>();
        for (var i = 0; i < 40; i++)
        {
            var evt = await ticker.Reader.ReadAsync(cts.Token);
            drifts.Add(evt.Drift);
        }

        var firstMedian = drifts.Take(20).Order().ElementAt(10);
        var secondMedian = drifts.Skip(20).Order().ElementAt(10);
        Assert.True(
            secondMedian <= firstMedian + TimeSpan.FromMilliseconds(5),
            $"Median drift grew from {firstMedian.TotalMilliseconds:F3} ms to {secondMedian.TotalMilliseconds:F3} ms.");
    }

    [Fact]
    public async Task Reader_Running_EmitsSequentialEvents()
    {
        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(20));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

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
    public async Task Reader_TenTicks_TotalElapsedMatchesInterval()
    {
        var interval = TimeSpan.FromMilliseconds(50);

        // The schedule origin is the moment of construction, so start timing before it.
        var sw = Stopwatch.StartNew();
        using var ticker = new GhostTicker(interval);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        // Collect 10 ticks and measure total elapsed time.
        for (var i = 0; i < 10; i++)
            await ticker.Reader.ReadAsync(cts.Token);
        sw.Stop();

        var expected = interval * 10;
        // Drift correction keeps the total near 10 intervals. Ticks must never be early (tight lower
        // bound); the upper bound is generous so a one-off scheduling stall cannot fail the test.
        Assert.InRange(sw.Elapsed, expected - TimeSpan.FromMilliseconds(5), expected + TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Verifies that disposing immediately after construction, racing with thread start-up, neither
    /// throws nor leaves the channel open.
    /// </summary>
    [Fact]
    public async Task Dispose_ImmediatelyAfterConstruction_CompletesChannel()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        for (var i = 0; i < 200; i++)
        {
            var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));
            ticker.Dispose();

            while (await ticker.Reader.WaitToReadAsync(cts.Token))
            {
                ticker.Reader.TryRead(out _);
            }
        }
    }

    /// <summary>
    /// Verifies that <see cref="GhostTicker.Stop"/> is a safe no-op after <see cref="GhostTicker.Dispose"/>.
    /// </summary>
    [Fact]
    public void Stop_AfterDispose_DoesNotThrow()
    {
        var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));
        ticker.Dispose();

        ticker.Stop();
        ticker.Stop();
    }

    /// <summary>
    /// Verifies that <see cref="GhostTicker.Stop"/> interrupts a long sleep and completes the channel promptly.
    /// </summary>
    [Fact]
    public async Task Stop_LongInterval_CompletesChannelPromptly()
    {
        // Arrange: the timer thread is parked in its long sleep phase.
        using var ticker = new GhostTicker(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        // Act
        var stopwatch = Stopwatch.StartNew();
        ticker.Stop();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var hasMore = await ticker.Reader.WaitToReadAsync(cts.Token);
        stopwatch.Stop();

        // Assert
        Assert.False(hasMore);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Stop took {stopwatch.Elapsed}.");
    }

    /// <summary>
    /// Verifies that <see cref="GhostTicker.Dispose"/> interrupts a long sleep and completes the channel promptly.
    /// </summary>
    [Fact]
    public async Task Dispose_LongInterval_CompletesChannelPromptly()
    {
        var ticker = new GhostTicker(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        ticker.Dispose();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        Assert.False(await ticker.Reader.WaitToReadAsync(cts.Token));
    }

    /// <summary>
    /// Verifies that a zero or negative channel capacity is rejected.
    /// </summary>
    /// <param name="capacity">The invalid capacity to test.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveChannelCapacity_Throws(int capacity)
    {
        var opts = new GhostTickerOptions { ChannelCapacity = capacity };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new GhostTicker(TimeSpan.FromMilliseconds(10), opts));
        Assert.Equal("options", ex.ParamName);
    }

    /// <summary>
    /// Verifies that a negative spin threshold is rejected.
    /// </summary>
    [Fact]
    public void Constructor_NegativeSpinThreshold_Throws()
    {
        var opts = new GhostTickerOptions { SpinThreshold = TimeSpan.FromMilliseconds(-1) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new GhostTicker(TimeSpan.FromMilliseconds(10), opts));
        Assert.Equal("options", ex.ParamName);
    }

    /// <summary>
    /// Verifies that mutating one <see cref="GhostTickerOptions.Default"/> instance does not affect later ones.
    /// </summary>
    [Fact]
    public void Default_ReturnsIndependentInstances()
    {
        var first = GhostTickerOptions.Default;
        first.ChannelCapacity = 99;

        Assert.Equal(1, GhostTickerOptions.Default.ChannelCapacity);
    }

    /// <summary>
    /// Verifies that the first tick is scheduled one interval after construction, not after thread start-up.
    /// </summary>
    [Fact]
    public async Task Reader_FirstTick_FiresNoEarlierThanIntervalAfterConstruction()
    {
        var interval = TimeSpan.FromMilliseconds(50);
        var before = DateTimeOffset.UtcNow;

        using var ticker = new GhostTicker(interval);
        var evt = await ticker.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.True(evt.ScheduledAt >= before + interval,
            $"First tick was scheduled at {evt.ScheduledAt:O}, expected no earlier than {before + interval:O}.");
    }

    /// <summary>
    /// Verifies that the median drift stays well below the default Windows timer granularity (about 15.6 ms).
    /// </summary>
    [Fact]
    public async Task Reader_ShortInterval_MedianDriftBelowThreshold()
    {
        // 10 ms sits below the default ~15.6 ms Windows timer granularity, so a plain Sleep-based
        // wait would be late by several milliseconds here. A single bound on the median (robust to
        // outliers) catches that regression without depending on the platform or CI environment.
        var threshold = TimeSpan.FromMilliseconds(8);

        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(10));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var drifts = new List<TimeSpan>();
        for (var i = 0; i < 50; i++)
        {
            var evt = await ticker.Reader.ReadAsync(cts.Token);
            drifts.Add(evt.Drift);
        }

        drifts.Sort();
        var median = drifts[drifts.Count / 2];
        Assert.True(median < threshold, $"Median drift was {median.TotalMilliseconds:F3} ms (threshold {threshold.TotalMilliseconds} ms).");
    }

    /// <summary>
    /// Verifies that scheduled times are exact multiples of the interval from the origin, with no accumulated rounding.
    /// </summary>
    [Fact]
    public async Task Reader_NonRoundInterval_ScheduledAtIsExactMultipleOfInterval()
    {
        // An interval that is not a whole number of Stopwatch ticks or milliseconds: the schedule
        // must still be exactly seq * interval from the origin, with no accumulated rounding.
        var interval = TimeSpan.FromTicks(333_333);
        using var ticker = new GhostTicker(interval);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var first = await ticker.Reader.ReadAsync(cts.Token);
        for (var i = 0; i < 20; i++)
        {
            var evt = await ticker.Reader.ReadAsync(cts.Token);

            var expected = interval * (long)(evt.Sequence - first.Sequence);
            Assert.Equal(expected, evt.ScheduledAt - first.ScheduledAt);
        }
    }

    /// <summary>
    /// Verifies that an interval above <see cref="GhostTicker.MaxInterval"/> is rejected.
    /// </summary>
    [Fact]
    public void Constructor_IntervalAboveMax_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new GhostTicker(GhostTicker.MaxInterval + TimeSpan.FromTicks(1)));
        Assert.Equal("interval", ex.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GhostTicker(TimeSpan.MaxValue));
    }

    /// <summary>
    /// Verifies that an interval equal to <see cref="GhostTicker.MaxInterval"/> is accepted.
    /// </summary>
    [Fact]
    public void Constructor_IntervalAtMax_DoesNotThrow()
    {
        using var ticker = new GhostTicker(GhostTicker.MaxInterval);
        ticker.Stop();
    }

    /// <summary>
    /// Verifies that an undefined <see cref="ThreadPriority"/> value is rejected.
    /// </summary>
    [Fact]
    public void Constructor_UndefinedThreadPriority_Throws()
    {
        var opts = new GhostTickerOptions { ThreadPriority = (ThreadPriority)99 };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new GhostTicker(TimeSpan.FromMilliseconds(10), opts));
        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Constructor_ZeroInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GhostTicker(TimeSpan.Zero));
    }
}