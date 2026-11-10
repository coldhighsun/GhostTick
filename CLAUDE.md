# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build (net10.0 only)
dotnet build -c Release

# Run all tests
dotnet test -c Release

# Run a single test by name (filter supports wildcards)
dotnet test -c Release --filter "FullyQualifiedName~Dispose_CalledTwice_DoesNotThrow"

# Run benchmarks (must be Release)
dotnet run --project benchmarks/GhostTick.Benchmarks -c Release

# Run examples
dotnet run --project examples/GhostTick.Examples
```

## Architecture

```
src/GhostTick/           # Library (net10.0)
  TimerEvent.cs          # readonly struct: ScheduledAt, FiredAt, Sequence, Drift
  GhostTicker.cs         # Repeating ticker → ChannelReader<TimerEvent>
  GhostTickerOptions.cs  # SpinThreshold, ChannelCapacity, FullMode, ThreadPriority, ThreadName
  Internal/
    PrecisionWaiter.cs   # Core timing primitive (hybrid sleep + spin)

tests/GhostTick.Tests/   # xUnit, net10.0
  GhostTickerTests.cs
```

### Precision Strategy

`PrecisionWaiter.WaitUntil` is the single timing primitive (one instance per ticker thread):
1. Compute `remaining = targetTimestamp - Stopwatch.GetTimestamp()`
2. If `remaining > spinThreshold`, sleep `(remaining - spinThreshold)` ms. On Windows this uses a high-resolution waitable timer (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, Win10 1803+) waited on together with the cancellation handle, because plain `Sleep`/`WaitOne` is bound to the ~15.6 ms system tick and can overshoot the target. Elsewhere (or if the timer cannot be created) it falls back to `ct.WaitHandle.WaitOne`.
3. Otherwise busy-spin with `Thread.SpinWait(10)` until the timestamp is reached

Default `SpinThreshold` is 1.5 ms. The sleep phase is interruptible by cancellation; only the final spin (at most `SpinThreshold`) is not, so stop latency is bounded by the spin threshold.

### Drift Correction

The time origin (`startTs`/`startTime`) is captured in the constructor, not on the timer thread. Each tick target is computed with exact 128-bit integer math as:
```
targetTs = startTs + seq * interval.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond
```
Not `lastFireTs + intervalTicks`, and not a pre-truncated per-interval tick count, so neither runtime duration nor rounding accumulates drift.

### Channel Semantics

`GhostTicker` uses a `BoundedChannel<TimerEvent>` (capacity 1 by default):
- `BoundedChannelFullMode.DropOldest` by default — slow consumers lose old ticks, not new ones. Gaps in `Sequence` reveal how many ticks were dropped.
- `FullMode = Wait` has no effect on the timer thread — `TickLoop` always calls `TryWrite` (never `WriteAsync`), so ticks are still dropped when the channel is full regardless of this setting.
- The channel completes (reader returns `false` from `WaitToReadAsync`) when `Stop()` or `Dispose()` is called.

### Threading

`GhostTicker` owns a dedicated background `Thread` (default `ThreadPriority.AboveNormal`) to avoid thread-pool starvation at high tick rates. The `CancellationToken` is captured as a struct value at construction time so the thread closure never touches `_cts` after disposal. `Dispose()` is idempotent via an `Interlocked.Exchange` guard.

### Versioning

NuGet package version is derived automatically from git tags by MinVer (prefix `v`, e.g. `v1.2.3`).
