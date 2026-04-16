using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using System.Diagnostics;

namespace GhostTick.Benchmarks;

/// <summary>
/// Measures inter-tick jitter for repeating timers.
/// Collects <see cref="TickCount"/> consecutive intervals and reports
/// their standard deviation in microseconds.
///
/// Lower stddev = more consistent cadence.
/// </summary>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class JitterBenchmark
{
    private const int TickCount = 100;

    [Params(1, 10, 50)]
    public int IntervalMs
    {
        get; set;
    }

    /// <summary>
    /// Baseline benchmark for GhostTicker jitter.  Measures the standard deviation of intervals between consecutive ticks.
    /// </summary>
    /// <returns>A task that represents the asynchronous benchmark operation, yielding the standard deviation of tick intervals in microseconds.</returns>
    [Benchmark(Baseline = true, Description = "GhostTicker")]
    public async Task<double> GhostTicker_Jitter()
    {
        var interval = TimeSpan.FromMilliseconds(IntervalMs);
        using var ticker = new GhostTicker(interval);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var timestamps = new long[TickCount];
        for (var i = 0; i < TickCount; i++)
        {
            await ticker.Reader.ReadAsync(cts.Token);
            timestamps[i] = Stopwatch.GetTimestamp();
        }
        ticker.Stop();

        return IntervalStdDevMicros(timestamps);
    }

    /// <summary>
    /// Measures the jitter of PeriodicTimer by collecting timestamps of consecutive ticks and computing their standard deviation.
    /// </summary>
    /// <returns>A task that represents the asynchronous benchmark operation, yielding the standard deviation of tick intervals in microseconds.</returns>
    [Benchmark(Description = "PeriodicTimer")]
    public async Task<double> PeriodicTimer_Jitter()
    {
        var interval = TimeSpan.FromMilliseconds(IntervalMs);
        using var pt = new PeriodicTimer(interval);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var timestamps = new long[TickCount];
        for (var i = 0; i < TickCount; i++)
        {
            await pt.WaitForNextTickAsync(cts.Token);
            timestamps[i] = Stopwatch.GetTimestamp();
        }

        return IntervalStdDevMicros(timestamps);
    }

    /// <summary>
    /// Measures the timing jitter of periodic callbacks using a System.Threading.Timer and returns the standard
    /// deviation of the interval durations in microseconds.
    /// </summary>
    /// <returns>A double representing the standard deviation, in microseconds, of the intervals between timer callbacks.</returns>
    [Benchmark(Description = "Threading.Timer")]
    public async Task<double> ThreadingTimer_Jitter()
    {
        var interval = TimeSpan.FromMilliseconds(IntervalMs);
        var channel = System.Threading.Channels.Channel.CreateBounded<long>(TickCount);

        await using var t = new Timer(_ =>
        {
            channel.Writer.TryWrite(Stopwatch.GetTimestamp());
        }, null, interval, interval);

        var timestamps = new long[TickCount];
        for (var i = 0; i < TickCount; i++)
            timestamps[i] = await channel.Reader.ReadAsync();

        t.Change(Timeout.Infinite, Timeout.Infinite);
        return IntervalStdDevMicros(timestamps);
    }

    /// <summary>
    /// Calculates the standard deviation of intervals, in microseconds, between consecutive timestamp values.
    /// </summary>
    /// <param name="timestamps">
    /// An array of timestamp values, in stopwatch ticks, representing sequential events. Must contain at least two elements.
    /// </param>
    /// <returns>The standard deviation, in microseconds, of the intervals between each pair of consecutive timestamps.</returns>
    private static double IntervalStdDevMicros(long[] timestamps)
    {
        // Compute intervals between consecutive timestamps.
        var intervals = new double[timestamps.Length - 1];
        for (var i = 0; i < intervals.Length; i++)
            intervals[i] = (timestamps[i + 1] - timestamps[i]) * 1_000_000.0 / Stopwatch.Frequency;

        var mean = 0.0;
        foreach (var v in intervals)
            mean += v;
        mean /= intervals.Length;

        var variance = 0.0;
        foreach (var v in intervals)
            variance += (v - mean) * (v - mean);
        variance /= intervals.Length;

        return Math.Sqrt(variance); // stddev in µs
    }

    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithWarmupCount(2)
                .WithIterationCount(10));
        }
    }
}