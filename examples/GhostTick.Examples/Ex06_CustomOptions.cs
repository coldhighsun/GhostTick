using System.Threading.Channels;

namespace GhostTick.Examples;

/// <summary>
/// Example 06 — Custom GhostTickerOptions
///
/// Demonstrates every tuning knob:
///   • SpinThreshold  — controls the CPU-vs-precision trade-off.
///   • ChannelCapacity / FullMode — back-pressure policy for slow consumers.
///   • ThreadPriority / ThreadName — thread tuning for the ticker loop.
/// </summary>
internal static class Ex06CustomOptions
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Ex08: Custom options ===");

        // --- GhostTickerOptions ---
        var tickerOpts = new GhostTickerOptions
        {
            SpinThreshold = TimeSpan.FromMilliseconds(2),
            ChannelCapacity = 8,                          // buffer up to 8 pending ticks
            FullMode = BoundedChannelFullMode.DropNewest, // keep oldest when full
            ThreadPriority = ThreadPriority.Highest,
            ThreadName = "MyHighPriorityTicker",
        };

        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(50), tickerOpts);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));

        try
        {
            await foreach (var evt in ticker.Reader.ReadAllAsync(cts.Token))
            {
                Console.WriteLine(
                    $"  Ticker (SpinThreshold=2 ms, Highest priority)  " +
                    $"#{evt.Sequence,2}  drift={evt.Drift.TotalMilliseconds,5:F2} ms");
            }
        }
        catch (OperationCanceledException) { }

        Console.WriteLine();
    }
}