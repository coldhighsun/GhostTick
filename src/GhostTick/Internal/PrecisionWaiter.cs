using System.Diagnostics;

namespace GhostTick.Internal;

/// <summary>
/// Utility for waiting until a specific timestamp with high precision.
/// </summary>
internal static class PrecisionWaiter
{
    /// <summary>
    /// Block the calling thread until <paramref name="targetTimestamp"/> (in <see cref="Stopwatch"/> ticks) is reached.
    /// </summary>
    /// <param name="targetTimestamp">Target expressed as <see cref="Stopwatch.GetTimestamp()"/> value.</param>
    /// <param name="spinThreshold">
    /// How far before the target to switch from sleeping to busy-spinning.
    /// Larger values increase CPU usage but reduce late-fire probability.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public static void WaitUntil(long targetTimestamp, TimeSpan spinThreshold, CancellationToken ct)
    {
        var spinTicks = (long)(spinThreshold.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = targetTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return;

            if (remaining > spinTicks)
            {
                // Convert the "sleep-able" portion to milliseconds.
                // We deliberately floor (not round) to avoid overshooting.
                var sleepMs = (remaining - spinTicks) * 1000L / Stopwatch.Frequency;
                if (sleepMs >= 1)
                {
                    Thread.Sleep((int)sleepMs);
                    continue;
                }
            }

            // Final stretch: tight spin without per-iteration cancellation checks or
            // branch overhead — the spin window is at most spinThreshold (default 1.5 ms),
            // so cancellation latency here is negligible.
            while (Stopwatch.GetTimestamp() < targetTimestamp)
                Thread.SpinWait(10);
            return;
        }
    }
}