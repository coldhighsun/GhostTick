namespace GhostTick.Examples;

/// <summary>
/// Example 04 — Cancellation and Stop
///
/// Shows two cancellation patterns:
///   A) Passing a CancellationToken to ReadAllAsync — the consumer stops
///      reading but the ticker keeps ticking until disposed.
///   B) Calling Stop() — the channel is completed and the ticker thread
///      exits cleanly.
/// </summary>
internal static class Ex04CancellationAndStop
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Ex06: Cancellation and Stop ===");

        // --- Pattern A: cancel the reader, not the ticker ---
        Console.WriteLine("  Pattern A — cancel reader via CancellationToken:");
        {
            using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(100));
            using var readerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));

            var count = 0;
            try
            {
                await foreach (var evt in ticker.Reader.ReadAllAsync(readerCts.Token))
                {
                    Console.WriteLine($"    #{evt.Sequence} received");
                    count++;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"    Reader cancelled after {count} tick(s) — ticker is still alive");
            }
        } // ticker disposed here

        // --- Pattern B: Stop() completes the channel ---
        Console.WriteLine("  Pattern B — Stop() completes the channel:");
        {
            using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(100));

            var count = 0;
            await foreach (var evt in ticker.Reader.ReadAllAsync())
            {
                Console.WriteLine($"    #{evt.Sequence} received");
                if (++count >= 3)
                {
                    ticker.Stop();  // signals the tick loop to exit
                    // ReadAllAsync will drain and then complete naturally
                }
            }
            Console.WriteLine($"    Channel completed after Stop()");
        }

        Console.WriteLine();
    }
}