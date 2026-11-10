using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace GhostTick.Benchmarks;

/// <summary>
/// Measures fire accuracy (drift) for each timer implementation.
/// </summary>
/// <remarks>
/// BenchmarkDotNet's own Mean/Error/StdDev columns measure total method duration, not the error
/// between scheduled and actual fire time, so they are hidden. Each benchmark method instead
/// collects <see cref="Samples"/> drift values (µs) which are published through
/// <see cref="DriftColumn"/> columns, so BenchmarkDotNet's regular exporters (Markdown, CSV, HTML)
/// write the report. The benchmark runs in-process so the columns can read the collected samples.
/// </remarks>
[Config(typeof(Config))]
public class PrecisionBenchmark
{
    /// <summary>
    /// Number of drift samples collected per method and delay.
    /// </summary>
    private const int Samples = 100;

    /// <summary>
    /// Number of discarded warmup waits performed before sampling.
    /// </summary>
    private const int Warmup = 5;

    /// <summary>
    /// Name of the <see cref="DelayMs"/> parameter, used to look up results from the columns.
    /// </summary>
    private const string DelayParameterName = nameof(DelayMs);

    /// <summary>
    /// Drift samples (µs) of the most recent invocation, keyed by benchmark method name and delay.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Method, int DelayMs), double[]> Results = new();

    /// <summary>
    /// Gets or sets the scheduled delay in milliseconds.
    /// </summary>
    [Params(1, 5, 10)]
    public int DelayMs
    {
        get; set;
    }

    /// <summary>
    /// Measures the fire error of <see cref="GhostTicker"/> using each tick's own drift.
    /// </summary>
    /// <returns>A task that represents the asynchronous benchmark operation.</returns>
    [Benchmark(Description = "GhostTicker")]
    public async Task GhostTicker()
    {
        var interval = TimeSpan.FromMilliseconds(DelayMs);

        // Warmup: run one ticker for a few ticks then discard.
        using (var warmup = new GhostTicker(interval))
        {
            for (var i = 0; i < Warmup; i++)
            {
                await warmup.Reader.ReadAsync();
            }

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
        Record(nameof(GhostTicker), drifts);
    }

    /// <summary>
    /// Measures the fire error of <see cref="Task.Delay(TimeSpan)"/>.
    /// </summary>
    /// <returns>A task that represents the asynchronous benchmark operation.</returns>
    [Benchmark(Description = "Task.Delay")]
    public async Task TaskDelay()
    {
        var delay = TimeSpan.FromMilliseconds(DelayMs);

        for (var i = 0; i < Warmup; i++)
        {
            await Task.Delay(delay);
        }

        var drifts = new double[Samples];
        var intervalTicks = (long)(delay.TotalSeconds * Stopwatch.Frequency);
        for (var i = 0; i < Samples; i++)
        {
            var refTs = Stopwatch.GetTimestamp();
            await Task.Delay(delay);
            var elapsed = Stopwatch.GetTimestamp() - refTs;
            drifts[i] = (elapsed - intervalTicks) * 1_000_000.0 / Stopwatch.Frequency;
        }

        Record(nameof(TaskDelay), drifts);
    }

    /// <summary>
    /// Measures the fire error of a one-shot <see cref="Timer"/>.
    /// </summary>
    /// <returns>A task that represents the asynchronous benchmark operation.</returns>
    [Benchmark(Description = "Threading.Timer")]
    public async Task ThreadingTimer()
    {
        var delay = TimeSpan.FromMilliseconds(DelayMs);

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

        Record(nameof(ThreadingTimer), drifts);
    }

    /// <summary>
    /// Measures the fire error of a one-shot <see cref="System.Timers.Timer"/>.
    /// </summary>
    /// <returns>A task that represents the asynchronous benchmark operation.</returns>
    [Benchmark(Description = "Timers.Timer")]
    public async Task TimersTimer()
    {
        var delay = TimeSpan.FromMilliseconds(DelayMs);

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

        Record(nameof(TimersTimer), drifts);
    }

    /// <summary>
    /// Stores the drift samples of the current invocation so the report columns can read them.
    /// </summary>
    /// <param name="method">The benchmark method name.</param>
    /// <param name="drifts">The drift samples in microseconds.</param>
    private void Record(string method, double[] drifts)
    {
        Results[(method, DelayMs)] = drifts;
    }

    /// <summary>
    /// BenchmarkDotNet configuration: a single in-process measured run per case and drift columns
    /// in place of the duration statistics.
    /// </summary>
    private sealed class Config : ManualConfig
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Config"/> class.
        /// </summary>
        public Config()
        {
            // One invocation already collects all samples; the stats come from the drift values.
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithStrategy(RunStrategy.Monitoring)
                .WithLaunchCount(1)
                .WithWarmupCount(1)
                .WithIterationCount(1)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));

            HideColumns(Column.Mean, Column.Error, Column.StdDev);

            AddColumn(
                new DriftColumn("Min (µs)", "Smallest fire error", s => s.Min, 1),
                new DriftColumn("Mean (µs)", "Mean fire error", s => s.Mean, 2),
                new DriftColumn("StdDev (µs)", "Standard deviation of the fire error", s => s.StandardDeviation, 3),
                new DriftColumn("P95 (µs)", "95th percentile of the fire error", s => s.Percentiles.P95, 4),
                new DriftColumn("P99 (µs)", "99th percentile of the fire error", s => s.Percentiles.Percentile(99), 5),
                new DriftColumn("Max (µs)", "Largest fire error", s => s.Max, 6));
        }
    }

    /// <summary>
    /// Report column that shows one statistic of the drift samples collected by a benchmark case.
    /// </summary>
    /// <param name="name">The column header.</param>
    /// <param name="legend">The legend text printed under the summary table.</param>
    /// <param name="selector">Selects the statistic to display from the drift samples.</param>
    /// <param name="priority">The ordering of the column within its category.</param>
    private sealed class DriftColumn(
        string name,
        string legend,
        Func<Statistics, double> selector,
        int priority) : IColumn
    {
        /// <summary>
        /// Gets the unique column identifier.
        /// </summary>
        public string Id => $"Drift.{name}";

        /// <summary>
        /// Gets the column header.
        /// </summary>
        public string ColumnName => name;

        /// <summary>
        /// Gets the legend text.
        /// </summary>
        public string Legend => legend;

        /// <summary>
        /// Gets a value indicating whether the column is always shown.
        /// </summary>
        public bool AlwaysShow => true;

        /// <summary>
        /// Gets the column category.
        /// </summary>
        public ColumnCategory Category => ColumnCategory.Custom;

        /// <summary>
        /// Gets the ordering of the column within its category.
        /// </summary>
        public int PriorityInCategory => priority;

        /// <summary>
        /// Gets a value indicating whether the column values are numeric.
        /// </summary>
        public bool IsNumeric => true;

        /// <summary>
        /// Gets the unit type of the column values.
        /// </summary>
        public UnitType UnitType => UnitType.Dimensionless;

        /// <summary>
        /// Returns whether the column is available for the summary.
        /// </summary>
        /// <param name="summary">The benchmark summary.</param>
        /// <returns>Always <see langword="true"/>.</returns>
        public bool IsAvailable(Summary summary) => true;

        /// <summary>
        /// Returns whether the column is a default column.
        /// </summary>
        /// <param name="summary">The benchmark summary.</param>
        /// <param name="benchmarkCase">The benchmark case.</param>
        /// <returns>Always <see langword="false"/>.</returns>
        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        /// <summary>
        /// Gets the formatted statistic for a benchmark case.
        /// </summary>
        /// <param name="summary">The benchmark summary.</param>
        /// <param name="benchmarkCase">The benchmark case.</param>
        /// <returns>The formatted value, or <c>NA</c> when no samples were collected.</returns>
        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            var delayMs = Convert.ToInt32(
                benchmarkCase.Parameters.Items.Single(p => p.Name == DelayParameterName).Value);
            var method = benchmarkCase.Descriptor.WorkloadMethod.Name;

            if (!Results.TryGetValue((method, delayMs), out var drifts))
            {
                return "NA";
            }

            return selector(new Statistics(drifts)).ToString("N1", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Gets the formatted statistic for a benchmark case.
        /// </summary>
        /// <param name="summary">The benchmark summary.</param>
        /// <param name="benchmarkCase">The benchmark case.</param>
        /// <param name="style">The summary style (unused).</param>
        /// <returns>The formatted value.</returns>
        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
            => GetValue(summary, benchmarkCase);
    }
}
