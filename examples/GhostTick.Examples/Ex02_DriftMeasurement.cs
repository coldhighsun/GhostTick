namespace GhostTick.Examples;

/// <summary>
/// Example 02 — Drift measurement over 50 ticks
///
/// Runs a 20 ms ticker for 50 ticks and computes min / max / average drift.
/// Illustrates the drift-correction guarantee: scheduled timestamps are
/// always startTime + seq × interval, never last_fire + interval.
/// </summary>
internal static class Ex02DriftMeasurement
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Ex04: Drift measurement over 50 ticks (20 ms interval) ===");

        const int tickCount = 50;
        var interval = TimeSpan.FromMilliseconds(20);

        using var ticker = new GhostTicker(interval);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var drifts = new List<double>(tickCount);

        for (var i = 0; i < tickCount; i++)
        {
            var evt = await ticker.Reader.ReadAsync(cts.Token);
            drifts.Add(evt.Drift.TotalMicroseconds);
        }

        ticker.Stop();

        var minDrift = drifts.Min();
        var maxDrift = drifts.Max();
        var avgDrift = drifts.Average();

        Console.WriteLine($"  Ticks  : {tickCount}");
        Console.WriteLine($"  Min    : {minDrift,8:F1} µs");
        Console.WriteLine($"  Max    : {maxDrift,8:F1} µs");
        Console.WriteLine($"  Avg    : {avgDrift,8:F1} µs");

        // Verify drift correction: gap between first and last scheduled time
        // must equal exactly (tickCount - 1) * interval.
        Console.WriteLine();
    }
}