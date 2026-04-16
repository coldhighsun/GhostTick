using System.Diagnostics;
using System.Threading.Channels;
using GhostTick.Internal;

namespace GhostTick;

/// <summary>
/// Repeating high-precision ticker.  Publishes a <see cref="TimerEvent"/> on <see cref="Reader"/>
/// at every <c>interval</c>, starting <c>interval</c> after construction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drift correction:</b> each target timestamp is computed as
/// <c>startTimestamp + sequence * intervalTicks</c> rather than <c>lastFireTimestamp + intervalTicks</c>,
/// so accumulated error stays bounded regardless of how long the ticker runs.
/// </para>
/// <para>
/// <b>Slow consumers:</b> when the channel is full (consumer is behind) the oldest unread tick
/// is dropped by default (configurable via <see cref="GhostTickerOptions.FullMode"/>).
/// The <see cref="TimerEvent.Sequence"/> counter always reflects the true tick number,
/// making gaps detectable.
/// </para>
/// <para>
/// <b>Threading:</b> a dedicated background thread (not a thread-pool thread) runs the timing
/// loop so it cannot be starved by a saturated thread-pool.
/// </para>
/// </remarks>
public sealed class GhostTicker : IDisposable
{
    /// <summary>
    /// Channel for publishing timer events from the background thread to consumers.
    /// </summary>
    private readonly Channel<TimerEvent> _channel;

    /// <summary>
    /// Cancellation token source for signaling the background thread to stop.
    /// </summary>
    private readonly CancellationTokenSource _cts;

    /// <summary>
    /// Guards against double-dispose (0 = alive, 1 = disposed).
    /// </summary>
    private int _disposed;

    /// <param name="interval">Time between ticks. Must be positive.</param>
    /// <param name="options">Tuning options; pass <see langword="null"/> for defaults.</param>
    public GhostTicker(TimeSpan interval, GhostTickerOptions? options = null)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");

        var opts = options ?? GhostTickerOptions.Default;

        _channel = Channel.CreateBounded<TimerEvent>(new BoundedChannelOptions(opts.ChannelCapacity)
        {
            FullMode = opts.FullMode,
            SingleWriter = true,
            SingleReader = false,
        });

        _cts = new();

        // Capture the token as a struct value so the thread closure doesn't
        // access _cts after a potential Dispose() races with thread startup.
        var token = _cts.Token;

        var thread = new Thread(() => TickLoop(interval, opts.SpinThreshold, token))
        {
            IsBackground = true,
            Priority = opts.ThreadPriority,
            Name = opts.ThreadName ?? $"GhostTicker({interval.TotalMilliseconds:F2}ms)",
        };
        thread.Start();
    }

    /// <summary>
    /// Receive end of the event channel.  Completes when the ticker is stopped or disposed.
    /// </summary>
    public ChannelReader<TimerEvent> Reader => _channel.Reader;

    /// <summary>
    /// Stop the ticker and release resources.  Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    /// Stop the ticker and complete the channel.  Idempotent.
    /// </summary>
    public void Stop() => _cts.Cancel();

    /// <summary>
    /// Executes a periodic timer loop that generates and enqueues timer events at the specified interval until
    /// cancellation is requested.
    /// </summary>
    /// <param name="interval">The time interval between consecutive timer events. Must be a positive duration.</param>
    /// <param name="spinThreshold">
    /// The threshold duration used to determine when to switch from sleeping to spinning for precise timing. Must be a non-negative duration.
    /// </param>
    /// <param name="ct">A cancellation token that can be used to request termination of the timer loop.</param>
    private void TickLoop(TimeSpan interval, TimeSpan spinThreshold, CancellationToken ct)
    {
        var intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        var startTs = Stopwatch.GetTimestamp();
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            ulong seq = 0;
            while (!ct.IsCancellationRequested)
            {
                seq++;

                // Drift-corrected target: always measured from the origin, never from last fire.
                var targetTs = startTs + (long)((double)seq * intervalTicks);
                var scheduledAt = startTime + TimeSpan.FromTicks((long)((double)seq * interval.Ticks));

                PrecisionWaiter.WaitUntil(targetTs, spinThreshold, ct);

                var firedTs = Stopwatch.GetTimestamp();
                var firedAt = startTime + TimeSpan.FromSeconds((double)(firedTs - startTs) / Stopwatch.Frequency);
                var evt = new TimerEvent(scheduledAt, firedAt, seq);

                // TryWrite never blocks; drops according to FullMode when channel is full.
                _channel.Writer.TryWrite(evt);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }
}