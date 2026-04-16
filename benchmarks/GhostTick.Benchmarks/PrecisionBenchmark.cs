using System.Diagnostics;

namespace GhostTick.Benchmarks;

/// <summary>
/// Measures fire accuracy (drift) for each timer implementation.
///
/// BenchmarkDotNet is unsuitable here because it measures total method duration,
/// not the error between scheduled and actual fire time. Instead, we collect
/// <see cref="Samples"/> drift values per configuration and report
/// Min / Mean / StdDev / P95 / P99 / Max in microseconds.
/// </summary>
public static class PrecisionBenchmark
{
    private const int Samples = 100;
    private const int Warmup = 5;
    private static readonly int[] DelaysMs = [1, 5, 10];

    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  Precision Benchmark — fire error vs scheduled time (µs, lower is better)             ║");
        Console.WriteLine($"║  {Samples} samples per cell, {Warmup} warmup runs                                                  ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        PrintHeader();

        foreach (var delayMs in DelaysMs)
        {
            await PrintRow("GhostTicker", delayMs, () => MeasureGhostTicker(delayMs));
            await PrintRow("Task.Delay", delayMs, () => MeasureTaskDelay(delayMs));
            await PrintRow("Threading.Timer", delayMs, () => MeasureThreadingTimer(delayMs));
            await PrintRow("Timers.Timer", delayMs, () => MeasureTimersTimer(delayMs));
            Console.WriteLine($"  {"─".PadRight(79, '─')}");
        }

        Console.WriteLine();
    }

    // ── per-timer samplers ────────────────────────────────────────────────────

    private static async Task<double[]> MeasureGhostTicker(int delayMs)
    {
        var interval = TimeSpan.FromMilliseconds(delayMs);

        // Warmup: run one ticker for a few ticks then discard.
        using (var warmup = new GhostTicker(interval))
        {
            for (var i = 0; i < Warmup; i++)
                await warmup.Reader.ReadAsync();
            warmup.Stop();
        }

        // Each tick's Drift property is FiredAt - ScheduledAt, which is exactly
        // the per-tick fire error relative to the drift-corrected schedule.
        var drifts = new double[Samples];
        using var ticker = new GhostTicker(interval);
        for (var i = 0; i < Samples; i++)
        {
            var evt = await ticker.Reader.ReadAsync();
            drifts[i] = evt.Drift.TotalMicroseconds;
        }
        ticker.Stop();
        return drifts;
    }

    private static async Task<double[]> MeasureTaskDelay(int delayMs)
    {
        var delay = TimeSpan.FromMilliseconds(delayMs);

        for (var i = 0; i < Warmup; i++)
            await Task.Delay(delay);

        var drifts = new double[Samples];
        var intervalTicks = (long)(delay.TotalSeconds * Stopwatch.Frequency);
        for (var i = 0; i < Samples; i++)
        {
            var refTs = Stopwatch.GetTimestamp();
            await Task.Delay(delay);
            var elapsed = Stopwatch.GetTimestamp() - refTs;
            drifts[i] = (elapsed - intervalTicks) * 1_000_000.0 / Stopwatch.Frequency;
        }
        return drifts;
    }

    private static async Task<double[]> MeasureThreadingTimer(int delayMs)
    {
        var delay = TimeSpan.FromMilliseconds(delayMs);

        for (var i = 0; i < Warmup; i++)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var t = new Timer(_ => tcs.TrySetResult(), null, delay, Timeout.InfiniteTimeSpan);
            await tcs.Task;
        }

        var drifts = new double[Samples];
        var intervalTicks = (long)(delay.TotalSeconds * Stopwatch.Frequency);
        for (var i = 0; i < Samples; i++)
        {
            var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var refTs = Stopwatch.GetTimestamp();

            await using var t = new Timer(_ =>
            {
                var elapsed = Stopwatch.GetTimestamp() - refTs;
                tcs.TrySetResult((elapsed - intervalTicks) * 1_000_000.0 / Stopwatch.Frequency);
            }, null, delay, Timeout.InfiniteTimeSpan);

            drifts[i] = await tcs.Task;
        }
        return drifts;
    }

    private static async Task<double[]> MeasureTimersTimer(int delayMs)
    {
        var delay = TimeSpan.FromMilliseconds(delayMs);

        for (var i = 0; i < Warmup; i++)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var t = new System.Timers.Timer(delay);
            t.AutoReset = false;
            t.Elapsed += (_, _) => tcs.TrySetResult();
            t.Start();
            await tcs.Task;
        }

        var drifts = new double[Samples];
        var intervalTicks = (long)(delay.TotalSeconds * Stopwatch.Frequency);
        for (var i = 0; i < Samples; i++)
        {
            var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var refTs = Stopwatch.GetTimestamp();

            using var t = new System.Timers.Timer(delay);
            t.AutoReset = false;
            t.Elapsed += (_, _) =>
            {
                var elapsed = Stopwatch.GetTimestamp() - refTs;
                tcs.TrySetResult((elapsed - intervalTicks) * 1_000_000.0 / Stopwatch.Frequency);
            };
            t.Start();

            drifts[i] = await tcs.Task;
        }
        return drifts;
    }

    private static void PrintHeader()
    {
        Console.WriteLine($"  {"Method",-20} {"Delay",8}  {"Min",8} {"Mean",8} {"StdDev",8} {"P95",8} {"P99",8} {"Max",8}");
        Console.WriteLine($"  {"─".PadRight(79, '─')}");
    }

    private static async Task PrintRow(
        string label, int delayMs, Func<Task<double[]>> measure)
    {
        var drifts = await measure();
        var stats = new DriftStats(drifts);

        Console.WriteLine(
            $"  {label,-20} {delayMs,6} ms" +
            $"  {stats.Min,7:F1}" +
            $"  {stats.Mean,7:F1}" +
            $"  {stats.StdDev,7:F1}" +
            $"  {stats.P95,7:F1}" +
            $"  {stats.P99,7:F1}" +
            $"  {stats.Max,7:F1}");
    }

    private readonly struct DriftStats
    {
        public DriftStats(double[] values)
        {
            Array.Sort(values);
            Min = values[0];
            Max = values[^1];
            P95 = values[(int)(values.Length * 0.95)];
            P99 = values[(int)(values.Length * 0.99)];

            var sum = 0.0;
            foreach (var v in values)
                sum += v;
            Mean = sum / values.Length;

            var mean = Mean;
            var variance = 0.0;
            foreach (var v in values)
                variance += (v - mean) * (v - mean);
            StdDev = Math.Sqrt(variance / values.Length);
        }

        public double Max
        {
            get;
        }

        public double Mean
        {
            get;
        }

        public double Min
        {
            get;
        }

        public double P95
        {
            get;
        }

        public double P99
        {
            get;
        }

        public double StdDev
        {
            get;
        }
    }
}