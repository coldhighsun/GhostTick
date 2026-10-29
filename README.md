# GhostTick

[![CI](https://github.com/coldhighsun/GhostTick/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/GhostTick/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/GhostTick)](https://www.nuget.org/packages/GhostTick)
[![NuGet Downloads](https://img.shields.io/nuget/dt/GhostTick)](https://www.nuget.org/packages/GhostTick)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

A high-precision timer toolkit for .NET that delivers events through `System.Threading.Channels`.

## Features

- **Sub-millisecond precision** — hybrid sleep + busy-spin strategy; Reduce OS timer granularity from ~15 ms to ~1 ms on Windows
- **Channel-based API** — consumers receive `TimerEvent` values via `ChannelReader<TimerEvent>`, keeping the timer thread fully decoupled from consumer latency
- **Drift correction** — `GhostTicker` computes every target as `start + seq × interval`, so accumulated error stays bounded over time
- **Multi-targeting** — `netstandard2.0`, `net8.0`, `net10.0`

## Installation

```
dotnet add package GhostTick
```

## Quick start

### Repeating ticker

```csharp
using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(16.67)); // ~60 fps
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

await foreach (var evt in ticker.Reader.ReadAllAsync(cts.Token))
{
    // evt.Sequence — monotonic tick counter (gaps indicate dropped ticks)
    // evt.Drift    — how late this tick fired vs its scheduled time
    Render(evt.Sequence);
}
```

## API reference

### `TimerEvent`

| Member | Type | Description |
|---|---|---|
| `ScheduledAt` | `DateTimeOffset` | UTC time the event was expected to fire |
| `FiredAt` | `DateTimeOffset` | UTC time the event actually fired (Stopwatch-derived, sub-ms accurate) |
| `Sequence` | `ulong` | Increments per tick |
| `Drift` | `TimeSpan` | `FiredAt − ScheduledAt` — positive means late |

### `GhostTicker`

```csharp
new GhostTicker(TimeSpan interval, GhostTickerOptions? options = null)
```

| Member | Description |
|---|---|
| `Reader` | `ChannelReader<TimerEvent>` — completes on stop |
| `Stop()` | Signal the tick loop to exit and complete the channel |
| `Dispose()` | Same as `Stop()` |

### Options

**`GhostTickerOptions`**

| Property | Default | Description |
|---|---|---|
| `SpinThreshold` | `1.5 ms` | Switch from sleep to busy-spin this far before each target |
| `ChannelCapacity` | `1` | Bounded channel capacity |
| `FullMode` | `DropOldest` | What to do when the channel is full (slow consumer) |
| `ThreadPriority` | `AboveNormal` | Priority of the dedicated ticker thread |
| `ThreadName` | auto | Name visible in debuggers / profilers |

## Slow consumers and back-pressure

The timer thread calls `TryWrite` and never blocks. When the channel is full:

- **`DropOldest`** (default) — oldest pending tick is discarded; consumer always gets the freshest event. Gaps in `Sequence` reveal how many ticks were dropped.
- **`DropNewest`** — new tick is discarded; consumer drains at its own pace.
- **`Wait`** — has no effect on the timer thread; `TryWrite` is always used so ticks are still dropped when the channel is full.

## Fan-out to multiple consumers

`ChannelReader<T>` delivers each item to exactly one reader. For broadcast semantics, dispatch manually:

```csharp
using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(100));

var ch1 = Channel.CreateBounded<TimerEvent>(4);
var ch2 = Channel.CreateBounded<TimerEvent>(4);

// Dispatcher
_ = Task.Run(async () =>
{
    await foreach (var evt in ticker.Reader.ReadAllAsync())
    {
        ch1.Writer.TryWrite(evt);
        ch2.Writer.TryWrite(evt);
    }
    ch1.Writer.Complete();
    ch2.Writer.Complete();
});

// Consumer A
_ = Task.Run(async () =>
{
    await foreach (var evt in ch1.Reader.ReadAllAsync())
        Console.WriteLine($"A #{evt.Sequence}");
});
```

## Benchmarks

> Environment: Windows 11, 13th Gen Intel Core i9-13980HX, .NET 10.0.6, BenchmarkDotNet v0.15.8

### Fire accuracy — error vs scheduled time (µs, lower is better)

100 samples per cell.

| Method | Delay | Min | Mean | StdDev | P95 | P99 |
|---|---:|---:|---:|---:|---:|---:|
| **GhostTicker** | 1 ms | **0.0** | **0.3** | **0.2** | **0.7** | **0.9** |
| Task.Delay | 1 ms | 12,549 | 14,623 | 565 | 15,238 | 16,326 |
| Threading.Timer | 1 ms | 13,260 | 14,545 | 470 | 15,362 | 15,784 |
| Timers.Timer | 1 ms | 12,078 | 14,674 | 560 | 15,372 | 16,535 |
| **GhostTicker** | 5 ms | **0.0** | **5,487** | **4,806** | **12,641** | **13,216** |
| Task.Delay | 5 ms | 4,492 | 10,559 | 983 | 11,205 | 16,578 |
| Threading.Timer | 5 ms | 9,627 | 10,665 | 457 | 11,166 | 11,929 |
| Timers.Timer | 5 ms | 9,783 | 10,539 | 440 | 11,177 | 11,478 |
| **GhostTicker** | 10 ms | **0.0** | **5,434** | **4,158** | **11,927** | **13,139** |
| Task.Delay | 10 ms | 4,507 | 5,603 | 505 | 6,145 | 6,996 |
| Threading.Timer | 10 ms | 4,781 | 5,684 | 480 | 6,253 | 7,190 |
| Timers.Timer | 10 ms | 2,701 | 5,552 | 641 | 6,359 | 8,460 |

At 1 ms delay, GhostTicker mean error is **0.3 µs** vs ~**14,600 µs** for all others — a **~49,000× improvement**.

### Tick cadence — 100 consecutive ticks, total wall time (ms, lower is better)

GhostTicker uses a dedicated `AboveNormal`-priority thread; others depend on the thread pool.

| Method | Interval | Mean | Ratio | Allocated |
|---|---:|---:|---:|---:|
| **GhostTicker** | 1 ms | **100.4 ms** | 1.00 | 16.27 KB |
| PeriodicTimer | 1 ms | 1,552.1 ms | 15.46× | 2.32 KB |
| Threading.Timer | 1 ms | 1,556.6 ms | 15.50× | 2.80 KB |
| **GhostTicker** | 10 ms | **1,007.7 ms** | 1.00 | 14.40 KB |
| PeriodicTimer | 10 ms | 1,557.2 ms | 1.55× | 2.32 KB |
| Threading.Timer | 10 ms | 1,557.5 ms | 1.55× | 2.80 KB |
| **GhostTicker** | 50 ms | 5,004.3 ms | 1.00 | 16.72 KB |
| PeriodicTimer | 50 ms | 5,008.6 ms | 1.00× | 2.32 KB |
| Threading.Timer | 50 ms | 5,008.5 ms | 1.00× | 14.85 KB |

Run benchmarks locally:
```
dotnet run --project benchmarks/GhostTick.Benchmarks -c Release
```

## Examples

See [`examples/GhostTick.Examples`](examples/GhostTick.Examples) for runnable examples covering all features.

```
dotnet run --project examples/GhostTick.Examples
```

## License

MIT
