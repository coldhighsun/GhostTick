namespace GhostTick.Examples;

/// <summary>
/// Example 01 — Repeating GhostTicker
///
/// Creates a 100 ms ticker and reads 8 ticks, printing each event's
/// sequence number and drift so you can see the precision in action.
/// </summary>
internal static class Ex01BasicTicker
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Ex03: Basic repeating ticker (100 ms) ===");

        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        try
        {
            await foreach (var evt in ticker.Reader.ReadAllAsync(cts.Token))
            {
                Console.WriteLine(
                    $"  #{evt.Sequence,2}  scheduled={evt.ScheduledAt:HH:mm:ss.ffffff}" +
                    $"  drift={evt.Drift.TotalMilliseconds,6:F2} ms");
            }
        }
        catch (OperationCanceledException) { }

        Console.WriteLine();
    }
}