# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build all target frameworks
dotnet build -c Release

# Run all tests (net8.0 / net9.0 / net10.0)
dotnet test -c Release

# Run a single test by name (filter supports wildcards)
dotnet test -c Release --filter "FullyQualifiedName~Stop_cancels_timer"

# Run tests for a specific TFM only
dotnet test -c Release -f net10.0
```

## Architecture

```
src/GhostTick/           # Library (netstandard2.0 + net8/9/10)
  TimerEvent.cs          # readonly struct: ScheduledAt, FiredAt, Sequence, Drift
  GhostTicker.cs         # Repeating ticker → ChannelReader<TimerEvent>
  GhostTickerOptions.cs  # SpinThreshold, ChannelCapacity, FullMode, ThreadPriority
  Internal/
    PrecisionWaiter.cs   # Core timing primitive (hybrid sleep + spin)

tests/GhostTick.Tests/   # xUnit, multi-targeted net8/9/10
  GhostTickerTests.cs
```

### Precision Strategy

`PrecisionWaiter.WaitUntil` is the single timing primitive used by `GhostTicker`:
1. Compute `remaining = targetTimestamp - Stopwatch.GetTimestamp()`
2. If `remaining > spinThreshold`, sleep `(remaining - spinThreshold)` ms via `Thread.Sleep`
3. Otherwise busy-spin with `Thread.SpinWait(10)` until the timestamp is reached

Default `SpinThreshold` is 1.5 ms — sufficient margin for Windows' ~15 ms OS timer granularity.

### Drift Correction (GhostTicker)

Each tick target is computed as:
```
targetTs = startTs + (long)((double)seq * intervalTicks)
```
Not `lastFireTs + intervalTicks`. This bounds accumulated drift regardless of runtime duration.

Note: the multiplication uses `double` arithmetic. For typical workloads (millisecond-range intervals, seq values well below 10⁹) the floating-point rounding error is sub-microsecond and inconsequential. At extremely large seq values the error is still bounded and does not accumulate across ticks.

### Channel Semantics

`GhostTicker` uses a `BoundedChannel<TimerEvent>` (capacity 1 by default, configurable):
- `BoundedChannelFullMode.DropOldest` by default — slow consumers lose old ticks, not new ones. `Sequence` gaps are detectable by the consumer.

### Threading

- `GhostTicker` owns a dedicated background `Thread` (default `ThreadPriority.AboveNormal`) to avoid thread-pool starvation at high tick rates.

### netstandard2.0 Compatibility

`System.Threading.Channels` is pulled in as a NuGet package for netstandard2.0 only (see the `Condition` in `GhostTick.csproj`). `IAsyncEnumerable<T>` / `ReadAllAsync()` requires netstandard2.1+ or net5+; on netstandard2.0 consumers use `WaitToReadAsync` + `TryRead`.
