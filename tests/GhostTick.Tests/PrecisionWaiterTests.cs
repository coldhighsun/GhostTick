using System.Diagnostics;
using GhostTick.Internal;

namespace GhostTick.Tests;

/// <summary>
/// Tests for the internal <see cref="PrecisionWaiter"/> timing primitive.
/// </summary>
public class PrecisionWaiterTests
{
    /// <summary>
    /// Converts a duration to <see cref="Stopwatch"/> ticks.
    /// </summary>
    /// <param name="duration">The duration to convert.</param>
    /// <returns>The equivalent number of <see cref="Stopwatch"/> ticks.</returns>
    private static long ToStopwatchTicks(TimeSpan duration) => (long)(duration.TotalSeconds * Stopwatch.Frequency);

    /// <summary>
    /// Verifies that a target already in the past returns without waiting.
    /// </summary>
    [Fact]
    public void WaitUntil_TargetInPast_ReturnsImmediately()
    {
        using var waiter = new PrecisionWaiter(TimeSpan.FromMilliseconds(1.5), CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        waiter.WaitUntil(Stopwatch.GetTimestamp() - ToStopwatchTicks(TimeSpan.FromSeconds(1)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100), $"Took {stopwatch.Elapsed}.");
    }

    /// <summary>
    /// Verifies that the wait never returns before the target timestamp.
    /// </summary>
    [Fact]
    public void WaitUntil_FutureTarget_NeverReturnsEarly()
    {
        using var waiter = new PrecisionWaiter(TimeSpan.FromMilliseconds(1.5), CancellationToken.None);
        var target = Stopwatch.GetTimestamp() + ToStopwatchTicks(TimeSpan.FromMilliseconds(30));

        waiter.WaitUntil(target);

        Assert.True(Stopwatch.GetTimestamp() >= target);
    }

    /// <summary>
    /// Verifies that a spin threshold beyond the <see cref="long"/> range spins to the target without overflowing.
    /// </summary>
    [Fact]
    public void WaitUntil_HugeSpinThreshold_SpinsUntilTargetWithoutOverflow()
    {
        // A threshold beyond the long range used to overflow the cast and give an undefined spin window.
        using var waiter = new PrecisionWaiter(TimeSpan.MaxValue, CancellationToken.None);
        var target = Stopwatch.GetTimestamp() + ToStopwatchTicks(TimeSpan.FromMilliseconds(20));

        waiter.WaitUntil(target);

        Assert.True(Stopwatch.GetTimestamp() >= target);
    }

    /// <summary>
    /// Verifies that an already cancelled token makes the wait throw <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact]
    public void WaitUntil_AlreadyCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var waiter = new PrecisionWaiter(TimeSpan.FromMilliseconds(1.5), cts.Token);

        Assert.Throws<OperationCanceledException>(
            () => waiter.WaitUntil(Stopwatch.GetTimestamp() + ToStopwatchTicks(TimeSpan.FromSeconds(10))));
    }

    /// <summary>
    /// Verifies that cancelling during the sleep phase wakes the wait promptly.
    /// </summary>
    [Fact]
    public void WaitUntil_CancelledDuringSleep_WakesPromptly()
    {
        using var cts = new CancellationTokenSource();
        using var waiter = new PrecisionWaiter(TimeSpan.FromMilliseconds(1.5), cts.Token);
        var target = Stopwatch.GetTimestamp() + ToStopwatchTicks(TimeSpan.FromSeconds(30));
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => waiter.WaitUntil(target));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {stopwatch.Elapsed}.");
    }

    /// <summary>
    /// Verifies that <see cref="PrecisionWaiter.Dispose"/> is idempotent.
    /// </summary>
    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var waiter = new PrecisionWaiter(TimeSpan.FromMilliseconds(1.5), CancellationToken.None);

        waiter.Dispose();
        waiter.Dispose();
    }
}
