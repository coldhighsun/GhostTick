namespace GhostTick.Examples;

/// <summary>
/// Example 03 — Slow consumer / tick dropping
///
/// Configures a 50 ms ticker but the consumer sleeps for 200 ms between
/// reads.  With ChannelCapacity=1 and FullMode=DropOldest the channel keeps
/// only the freshest tick; the Sequence gaps reveal how many ticks were
/// dropped.
/// </summary>
internal static class Ex03SlowConsumer
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Ex05: Slow consumer — tick dropping (50 ms tick, 200 ms consumer) ===");

        var opts = new GhostTickerOptions
        {
            ChannelCapacity = 1,
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
        };

        using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(50), opts);

        ulong? prevSeq = null;
        for (var i = 0; i < 5; i++)
        {
            // Consumer is deliberately slow.
            await Task.Delay(200);

            if (ticker.Reader.TryRead(out var evt))
            {
                var dropped = prevSeq.HasValue ? (long)evt.Sequence - (long)prevSeq.Value - 1 : 0;
                Console.WriteLine(
                    $"  Received #{evt.Sequence,2}  dropped since last read: {dropped,2}");
                prevSeq = evt.Sequence;
            }
        }

        Console.WriteLine();
    }
}