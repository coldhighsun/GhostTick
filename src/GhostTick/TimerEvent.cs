namespace GhostTick;

/// <summary>
/// Carries timing information for a single timer or ticker fire.
/// </summary>
public readonly struct TimerEvent(DateTimeOffset scheduledAt, DateTimeOffset firedAt, ulong sequence)
{
    /// <summary>
    /// Difference between <see cref="FiredAt"/> and <see cref="ScheduledAt"/>. Positive means late.
    /// </summary>
    public TimeSpan Drift => FiredAt - ScheduledAt;

    /// <summary>
    /// UTC time at which this event actually fired (measured just after the wait).
    /// </summary>
    public DateTimeOffset FiredAt { get; } = firedAt;

    /// <summary>
    /// UTC time at which this event was scheduled to fire.
    /// </summary>
    public DateTimeOffset ScheduledAt { get; } = scheduledAt;

    /// <summary>
    /// Monotonic sequence number.
    /// Increments by 1 for each <see cref="GhostTicker"/> tick (starting at 1).
    /// </summary>
    public ulong Sequence { get; } = sequence;

    /// <summary>
    /// Debug-friendly string representation of this event, showing the sequence number, scheduled and fired timestamps, and drift in microseconds.
    /// </summary>
    public override string ToString() =>
        $"[#{Sequence}] scheduled={ScheduledAt:O} fired={FiredAt:O} drift={Drift.TotalMilliseconds * 1000:F1}µs";
}