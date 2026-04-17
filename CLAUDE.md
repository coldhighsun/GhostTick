# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build all target frameworks
dotnet build -c Release

# Run all tests (net8.0 / net9.0 / net10.0)
dotnet test -c Release

# Run a single test by name (filter supports wildcards)
dotnet test -c Release --filter "FullyQualifiedName~Dispose_is_idempotent"

# Run tests for a specific TFM only
dotnet test -c Release -f net10.0

# Run benchmarks (must be Release)
dotnet run --project benchmarks/GhostTick.Benchmarks -c Release

# Run examples
dotnet run --project examples/GhostTick.Examples
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

`PrecisionWaiter.WaitUntil` is the single timing primitive:
1. Compute `remaining = targetTimestamp - Stopwatch.GetTimestamp()`
2. If `remaining > spinThreshold`, sleep `(remaining - spinThreshold)` ms via `Thread.Sleep`
3. Otherwise busy-spin with `Thread.SpinWait(10)` until the timestamp is reached

Default `SpinThreshold` is 1.5 ms — sufficient margin for Windows' ~15 ms OS timer granularity. Cancellation is only checked at the top of the outer loop, not inside the spin, so stop latency is bounded by the spin threshold.

### Drift Correction

Each tick target is computed as:
```
targetTs = startTs + (long)((double)seq * intervalTicks)
```
Not `lastFireTs + intervalTicks`. This bounds accumulated drift regardless of runtime duration. The `double` multiplication introduces sub-microsecond rounding error at typical seq values; it does not accumulate across ticks.

### Channel Semantics

`GhostTicker` uses a `BoundedChannel<TimerEvent>` (capacity 1 by default):
- `BoundedChannelFullMode.DropOldest` by default — slow consumers lose old ticks, not new ones. Gaps in `Sequence` reveal how many ticks were dropped.
- `FullMode = Wait` has no effect on the timer thread — `TickLoop` always calls `TryWrite` (never `WriteAsync`), so ticks are still dropped when the channel is full regardless of this setting.
- The channel completes (reader returns `false` from `WaitToReadAsync`) when `Stop()` or `Dispose()` is called.

### Threading

`GhostTicker` owns a dedicated background `Thread` (default `ThreadPriority.AboveNormal`) to avoid thread-pool starvation at high tick rates. The `CancellationToken` is captured as a struct value at construction time so the thread closure never touches `_cts` after disposal. `Dispose()` is idempotent via an `Interlocked.Exchange` guard.

### netstandard2.0 Compatibility

`System.Threading.Channels` is pulled in as a NuGet package for netstandard2.0 only (conditioned in `GhostTick.csproj`). `IAsyncEnumerable<T>` / `ReadAllAsync()` requires netstandard2.1+ or net5+; on netstandard2.0 consumers use `WaitToReadAsync` + `TryRead`.

### Versioning

NuGet package version is derived automatically from git tags by MinVer (prefix `v`, e.g. `v1.2.3`).
