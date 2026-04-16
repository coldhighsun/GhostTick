using System.Threading.Channels;

namespace GhostTick;

/// <summary>
/// Configuration for <see cref="GhostTicker"/>.
/// </summary>
public sealed class GhostTickerOptions
{
    /// <summary>
    /// Default options singleton (immutable after construction).
    /// </summary>
    public static readonly GhostTickerOptions Default = new();

    /// <summary>
    /// Capacity of the event channel.  When the consumer is slower than the tick rate
    /// the channel drops the oldest unread event according to <see cref="FullMode"/>.
    /// Default: 1.
    /// </summary>
    public int ChannelCapacity { get; set; } = 1;

    /// <summary>
    /// What to do when the channel is full.
    /// Default: <see cref="BoundedChannelFullMode.DropOldest"/> — keeps the freshest tick.
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.DropOldest;

    /// <summary>
    /// How long before each scheduled tick the waiter switches from OS sleep to busy-spin.
    /// Default: 1.5 ms.
    /// </summary>
    public TimeSpan SpinThreshold { get; set; } = TimeSpan.FromMilliseconds(1.5);

    /// <summary>
    /// Optional name shown in debuggers/profilers for the ticker thread.
    /// </summary>
    public string? ThreadName
    {
        get; set;
    }

    /// <summary>
    /// Priority of the dedicated ticker thread.
    /// Raising this helps the thread wake up on time on loaded systems.
    /// Default: <see cref="ThreadPriority.AboveNormal"/>.
    /// </summary>
    public ThreadPriority ThreadPriority { get; set; } = ThreadPriority.AboveNormal;
}