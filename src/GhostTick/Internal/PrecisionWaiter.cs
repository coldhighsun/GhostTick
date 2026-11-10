using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GhostTick.Internal;

/// <summary>
/// Waits until a specific timestamp with high precision using a hybrid sleep + spin strategy.
/// </summary>
/// <remarks>
/// On Windows the sleep phase uses a high-resolution waitable timer (Windows 10 1803+), because
/// <see cref="WaitHandle.WaitOne(int)"/> and <see cref="Thread.Sleep(int)"/> are bound to the system
/// timer tick (about 15.6 ms by default) and can overshoot the target by an entire tick.
/// When the high-resolution timer is unavailable, or on other platforms, it falls back to
/// waiting on the cancellation token's handle. That fallback is bound to the system timer tick on
/// Windows, so precision degrades: the sleep phase can overshoot the target by up to one tick
/// (about 15.6 ms) unless the interval and spin threshold are large enough to absorb it.
/// </remarks>
internal sealed partial class PrecisionWaiter : IDisposable
{
    /// <summary>
    /// <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c> flag for <c>CreateWaitableTimerExW</c>.
    /// </summary>
    private const uint CreateWaitableTimerHighResolution = 0x00000002;

    /// <summary>
    /// <c>TIMER_ALL_ACCESS</c> access mask for <c>CreateWaitableTimerExW</c>.
    /// </summary>
    private const uint TimerAllAccess = 0x1F0003;

    /// <summary>
    /// Longest single sleep issued by <see cref="WaitUntil(long)"/>; longer waits are split into
    /// several sleeps, which keeps every conversion (to 100 ns units, to an Int32 millisecond count) in range.
    /// </summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromDays(1);

    /// <summary>
    /// Number of <see cref="Stopwatch"/> ticks that make up the spin window.
    /// </summary>
    private readonly long _spinTicks;

    /// <summary>
    /// Cancellation token observed during the sleep phase.
    /// </summary>
    private readonly CancellationToken _ct;

    /// <summary>
    /// Wait handle owning the high-resolution timer, or <see langword="null"/> when unavailable.
    /// </summary>
    private readonly TimerWaitHandle? _timer;

    /// <summary>
    /// Handles waited on together during the sleep phase: the timer and the cancellation token.
    /// </summary>
    private readonly WaitHandle[]? _handles;

    /// <summary>
    /// Creates a waiter bound to a single cancellation token.
    /// </summary>
    /// <param name="spinThreshold">
    /// How far before the target to switch from sleeping to busy-spinning.
    /// Larger values increase CPU usage but reduce late-fire probability.
    /// </param>
    /// <param name="ct">Cancellation token that interrupts the sleep phase.</param>
    public PrecisionWaiter(TimeSpan spinThreshold, CancellationToken ct)
    {
        // A threshold too large for a long simply means "spin the whole time"; clamp instead of
        // letting the double-to-long cast overflow into an unspecified value.
        _spinTicks = (long)Math.Min(spinThreshold.TotalSeconds * Stopwatch.Frequency, long.MaxValue / 2.0);
        _ct = ct;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var handle = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                }
                else
                {
                    _timer = new TimerWaitHandle(handle);
                    _handles = [_timer, ct.WaitHandle];
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Kernel32 export unavailable: fall back to waiting on the cancellation handle.
            }
        }
    }

    /// <summary>
    /// Releases the high-resolution timer, if one was created.
    /// </summary>
    public void Dispose() => _timer?.Dispose();

    /// <summary>
    /// Block the calling thread until <paramref name="targetTimestamp"/> (in <see cref="Stopwatch"/> ticks) is reached.
    /// </summary>
    /// <param name="targetTimestamp">Target expressed as <see cref="Stopwatch.GetTimestamp()"/> value.</param>
    /// <exception cref="OperationCanceledException">Thrown when the cancellation token is cancelled.</exception>
    public void WaitUntil(long targetTimestamp)
    {
        while (true)
        {
            _ct.ThrowIfCancellationRequested();

            var remaining = targetTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            if (remaining > _spinTicks)
            {
                // Convert the "sleep-able" portion to milliseconds.
                // We deliberately floor (not round) to avoid overshooting, and compute in
                // floating point so extremely long intervals cannot overflow the integer math.
                var sleepMs = Math.Floor((remaining - _spinTicks) * 1000.0 / Stopwatch.Frequency);
                if (sleepMs >= 1)
                {
                    Sleep(TimeSpan.FromMilliseconds(Math.Min(sleepMs, MaxSleep.TotalMilliseconds)));
                    continue;
                }
            }

            // Final stretch: tight spin without per-iteration cancellation checks or
            // branch overhead — the spin window is at most spinThreshold (default 1.5 ms),
            // so cancellation latency here is negligible.
            while (Stopwatch.GetTimestamp() < targetTimestamp)
            {
                Thread.SpinWait(10);
            }

            return;
        }
    }

    /// <summary>
    /// Sleeps for roughly <paramref name="duration"/>, waking early on cancellation.
    /// The caller re-checks the clock afterwards, so waking early or late is harmless.
    /// </summary>
    /// <param name="duration">Sleep length; at least one millisecond and at most <see cref="MaxSleep"/>.</param>
    private void Sleep(TimeSpan duration)
    {
        if (_timer is not null && _handles is not null)
        {
            // Negative due time means relative, in 100 ns units (exactly TimeSpan ticks).
            var dueTime = -duration.Ticks;
            if (SetWaitableTimer(_timer.SafeWaitHandle, ref dueTime, 0, 0, 0, false))
            {
                WaitHandle.WaitAny(_handles);
                return;
            }
        }

        _ct.WaitHandle.WaitOne(duration);
    }

    /// <summary>
    /// A <see cref="WaitHandle"/> that owns a waitable timer handle, without allocating a throw-away event.
    /// </summary>
    private sealed class TimerWaitHandle : WaitHandle
    {
        /// <summary>
        /// Wraps an already created waitable timer handle; disposing this object closes it.
        /// </summary>
        /// <param name="handle">A valid waitable timer handle.</param>
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    /// <summary>
    /// Creates a waitable timer object. See the Win32 <c>CreateWaitableTimerExW</c> documentation.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW")]
    private static partial SafeWaitHandle CreateWaitableTimerExW(nint timerAttributes, nint timerName, uint flags, uint desiredAccess);

    /// <summary>
    /// Arms a waitable timer. See the Win32 <c>SetWaitableTimer</c> documentation.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "SetWaitableTimer")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        SafeWaitHandle timer,
        ref long dueTime,
        int period,
        nint completionRoutine,
        nint argToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
}
