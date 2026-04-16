namespace GhostTick.Examples;

/// <summary>
/// Example 05 — Multiple independent consumers sharing one ticker
///
/// A single GhostTicker feeds two concurrent consumer tasks.
/// Both call ReadAsync on the same ChannelReader; each tick is delivered
/// to exactly one consumer (channels are not broadcast).
///
/// For broadcast semantics you would fan-out manually — see how the
/// dispatcher task below forwards each tick to two separate channels.
/// </summary>
internal static class Ex05MultipleConsumers
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Ex07: Fan-out to multiple consumers ===");

        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Two dedicated channels — one per consumer.
        var ch1 = System.Threading.Channels.Channel.CreateBounded<TimerEvent>(4);
        var ch2 = System.Threading.Channels.Channel.CreateBounded<TimerEvent>(4);

        // Dispatcher: reads from ticker, writes to both fan-out channels.
        var dispatcher = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in ticker.Reader.ReadAllAsync(cts.Token))
                {
                    ch1.Writer.TryWrite(evt);
                    ch2.Writer.TryWrite(evt);
                }
            }
            catch (OperationCanceledException) { }

            ch1.Writer.TryComplete();
            ch2.Writer.TryComplete();
        }, cts.Token);

        // Consumer 1 — prints even sequences only.
        var consumer1 = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in ch1.Reader.ReadAllAsync(cts.Token))
                    if (evt.Sequence % 2 == 0)
                        Console.WriteLine($"  Consumer-1  #{evt.Sequence,2}  drift={evt.Drift.TotalMilliseconds,5:F1} ms");
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Consumer 2 — prints odd sequences only.
        var consumer2 = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in ch2.Reader.ReadAllAsync(cts.Token))
                    if (evt.Sequence % 2 != 0)
                        Console.WriteLine($"  Consumer-2  #{evt.Sequence,2}  drift={evt.Drift.TotalMilliseconds,5:F1} ms");
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.WhenAll(dispatcher, consumer1, consumer2);

        Console.WriteLine();
    }
}