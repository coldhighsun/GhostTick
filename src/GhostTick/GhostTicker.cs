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
    /// Longest supported tick interval.  Larger values would overflow the 64-bit timestamp or
    /// <see cref="DateTimeOffset"/> arithmetic used to compute tick targets.
    /// </summary>
    internal static readonly TimeSpan MaxInterval = TimeSpan.FromDays(365);

    /// <summary>
    /// Guards against double-dispose (0 = alive, 1 = disposed).
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Serializes <see cref="CancellationTokenSource.Cancel()"/> against <see cref="CancellationTokenSource.Dispose()"/>,
    /// which is not thread-safe with other members of the source.
    /// </summary>
    private readonly Lock _ctsLock = new();

    /// <summary>
    /// Set (under <see cref="_ctsLock"/>) once the timer thread has disposed <see cref="_cts"/>.
    /// </summary>
    private bool _ctsDisposed;

    /// <summary>
    /// Creates a ticker and immediately starts its background timing thread.
    /// </summary>
    /// <param name="interval">Time between ticks. Must be positive.</param>
    /// <param name="options">Tuning options; pass <see langword="null"/> for defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="interval"/> is not positive or exceeds 365 days, or <paramref name="options"/> contains an invalid value.
    /// </exception>
    public GhostTicker(TimeSpan interval, GhostTickerOptions? options = null)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        }

        if (interval > MaxInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, $"Interval must not exceed {MaxInterval}.");
        }

        var opts = options ?? GhostTickerOptions.Default;

        // Snapshot every option value once: the caller may share or modify the options object
        // concurrently, and what is validated must be exactly what is used.
        var channelCapacity = opts.ChannelCapacity;
        var fullMode = opts.FullMode;
        var spinThreshold = opts.SpinThreshold;
        var threadPriority = opts.ThreadPriority;
        var threadName = opts.ThreadName;

        if (channelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), channelCapacity, "ChannelCapacity must be positive.");
        }

        if (spinThreshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), spinThreshold, "SpinThreshold must not be negative.");
        }

        if (!Enum.IsDefined(threadPriority))
        {
            throw new ArgumentOutOfRangeException(nameof(options), threadPriority, "ThreadPriority is not a defined value.");
        }

        _channel = Channel.CreateBounded<TimerEvent>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = fullMode,
            SingleWriter = true,
            SingleReader = false,
        });

        _cts = new();

        // Capture the token as a struct value so the thread closure doesn't
        // access _cts after a potential Dispose() races with thread startup.
        var token = _cts.Token;

        // Capture the time origin here (not on the new thread) so that thread start-up latency
        // does not shift the whole schedule: the first tick is due `interval` after construction.
        var startTs = Stopwatch.GetTimestamp();
        var startTime = DateTimeOffset.UtcNow;

        var thread = new Thread(() => TickLoop(interval, spinThreshold, startTs, startTime, token))
        {
            IsBackground = true,
            Priority = threadPriority,
            Name = threadName ?? $"GhostTicker({interval.TotalMilliseconds:F2}ms)",
        };

        try
        {
            thread.Start();
        }
        catch
        {
            // The timer thread never ran, so nobody else will complete the channel or release the
            // token source. Clean up here, then let the caller see the original failure.
            _channel.Writer.TryComplete();
            ReleaseCts();
            throw;
        }
    }

    /// <summary>
    /// Receive end of the event channel.  Completes when the ticker is stopped or disposed.
    /// </summary>
    public ChannelReader<TimerEvent> Reader => _channel.Reader;

    /// <summary>
    /// Stop the ticker and release resources.  Idempotent.  Cleanup completes asynchronously on the
    /// timer thread; see the inline note in the implementation.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Cleanup is asynchronous: this only requests cancellation. The timer thread completes the
        // channel and disposes the CancellationTokenSource when it exits (it waits on the token's
        // WaitHandle, so the source cannot be disposed from here). Readers therefore observe
        // completion shortly after Dispose returns (bounded by the spin threshold), not before.
        TryCancel();
    }

    /// <summary>
    /// Stop the ticker and complete the channel.  Idempotent, and safe to call after <see cref="Dispose"/>.
    /// </summary>
    public void Stop() => TryCancel();

    /// <summary>
    /// Requests cancellation, doing nothing if the timer thread has already exited
    /// and released the cancellation token source (nothing is left to stop).
    /// </summary>
    private void TryCancel()
    {
        lock (_ctsLock)
        {
            if (!_ctsDisposed)
            {
                _cts.Cancel();
            }
        }
    }

    /// <summary>
    /// Executes a periodic timer loop that generates and enqueues timer events at the specified interval until
    /// cancellation is requested.
    /// </summary>
    /// <param name="interval">The time interval between consecutive timer events. Must be a positive duration.</param>
    /// <param name="spinThreshold">
    /// The threshold duration used to determine when to switch from sleeping to spinning for precise timing. Must be a non-negative duration.
    /// </param>
    /// <param name="startTs">The <see cref="Stopwatch.GetTimestamp()"/> value captured at construction (the schedule origin).</param>
    /// <param name="startTime">The UTC time captured together with <paramref name="startTs"/>.</param>
    /// <param name="ct">A cancellation token that can be used to request termination of the timer loop.</param>
    private void TickLoop(TimeSpan interval, TimeSpan spinThreshold, long startTs, DateTimeOffset startTime, CancellationToken ct)
    {
        PrecisionWaiter? waiter = null;

        try
        {
            waiter = new PrecisionWaiter(spinThreshold, ct);

            ulong seq = 0;
            while (!ct.IsCancellationRequested)
            {
                seq++;

                // Drift-corrected target: always measured from the origin, never from last fire.
                // Computed with exact 128-bit integer math so neither the per-tick interval
                // truncation nor floating-point rounding accumulates with the sequence number.
                var scheduledTicks = (long)((Int128)seq * interval.Ticks);
                var targetTs = startTs + (long)((Int128)seq * interval.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond);
                var scheduledAt = startTime + TimeSpan.FromTicks(scheduledTicks);

                waiter.WaitUntil(targetTs);

                var firedTs = Stopwatch.GetTimestamp();

                // The wait guarantees firedTs >= targetTs; clamp so that rounding to 100 ns
                // can never make an event appear to fire before it was scheduled.
                var firedTicks = Math.Max(
                    scheduledTicks,
                    (long)((Int128)(firedTs - startTs) * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
                var firedAt = startTime + TimeSpan.FromTicks(firedTicks);
                var evt = new TimerEvent(scheduledAt, firedAt, seq);

                // TryWrite never blocks; drops according to FullMode when channel is full.
                _channel.Writer.TryWrite(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via Stop() / Dispose().
        }
        catch (Exception ex)
        {
            // Surface unexpected failures to consumers instead of crashing the process
            // from a background thread; ReadAsync / WaitToReadAsync will rethrow it.
            _channel.Writer.TryComplete(ex);
        }
        finally
        {
            waiter?.Dispose();
            _channel.Writer.TryComplete();

            ReleaseCts();
        }
    }

    /// <summary>
    /// Disposes <see cref="_cts"/> and marks it released so later <see cref="Stop"/> / <see cref="Dispose"/> calls
    /// become no-ops.
    /// </summary>
    private void ReleaseCts()
    {
        lock (_ctsLock)
        {
            _ctsDisposed = true;
            _cts.Dispose();
        }
    }
}