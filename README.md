# GhostTick

[![CI](https://github.com/coldhighsun/GhostTick/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/GhostTick/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/GhostTick)](https://www.nuget.org/packages/GhostTick)
[![NuGet Downloads](https://img.shields.io/nuget/dt/GhostTick)](https://www.nuget.org/packages/GhostTick)

A high-precision timer toolkit for .NET that delivers events through `System.Threading.Channels`.

## Features

- **Sub-millisecond precision** — hybrid sleep + busy-spin strategy; on Windows automatically calls `timeBeginPeriod(1)` to reduce OS timer granularity from ~15 ms to ~1 ms
- **Channel-based API** — consumers receive `TimerEvent` values via `ChannelReader<TimerEvent>`, keeping the timer thread fully decoupled from consumer latency
- **Drift correction** — `GhostTicker` computes every target as `start + seq × interval`, so accumulated error stays bounded over time
- **Multi-targeting** — `netstandard2.0`, `net8.0`, `net9.0`, `net10.0`

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
- **`Wait`** — blocks the timer thread (not recommended for precision use).

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

100 samples per cell. GhostTicker uses `timeBeginPeriod(1)` + busy-spin; others rely on the default OS timer (~15 ms granularity on Windows).

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

---

# GhostTick（中文）

[![CI](https://github.com/coldhighsun/GhostTick/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/GhostTick/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/GhostTick)](https://www.nuget.org/packages/GhostTick)
[![NuGet Downloads](https://img.shields.io/nuget/dt/GhostTick)](https://www.nuget.org/packages/GhostTick)

适用于 .NET 的高精度计时工具库，通过 `System.Threading.Channels` 传递事件。

## 功能特性

- **亚毫秒级精度** — 采用休眠 + 忙等待混合策略；在 Windows 上自动调用 `timeBeginPeriod(1)`，将 OS 计时器粒度从约 15 ms 降低至约 1 ms
- **基于 Channel 的 API** — 消费者通过 `ChannelReader<TimerEvent>` 接收 `TimerEvent`，计时线程与消费者延迟完全解耦
- **漂移修正** — `GhostTicker` 将每个目标时刻计算为 `start + seq × interval`，无论运行多久，累积误差始终有界
- **多目标框架** — `netstandard2.0`、`net8.0`、`net9.0`、`net10.0`

## 安装

```
dotnet add package GhostTick
```

## 快速上手

### 周期性 Ticker

```csharp
using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(16.67)); // ~60 fps
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

await foreach (var evt in ticker.Reader.ReadAllAsync(cts.Token))
{
    // evt.Sequence — 单调递增的 tick 计数器（序号出现间隙表示有 tick 被丢弃）
    // evt.Drift    — 该 tick 相对于计划触发时刻的延迟
    Render(evt.Sequence);
}
```

## API 参考

### `TimerEvent`

| 成员 | 类型 | 说明 |
|---|---|---|
| `ScheduledAt` | `DateTimeOffset` | 事件预期触发的 UTC 时间 |
| `FiredAt` | `DateTimeOffset` | 事件实际触发的 UTC 时间（基于 Stopwatch，亚毫秒精度） |
| `Sequence` | `ulong` | 每次 tick 递增 |
| `Drift` | `TimeSpan` | `FiredAt − ScheduledAt`，正值表示延迟触发 |

### `GhostTicker`

```csharp
new GhostTicker(TimeSpan interval, GhostTickerOptions? options = null)
```

| 成员 | 说明 |
|---|---|
| `Reader` | `ChannelReader<TimerEvent>` — 停止后完成 |
| `Stop()` | 通知 tick 循环退出并完成 channel |
| `Dispose()` | 同 `Stop()` |

### 选项

**`GhostTickerOptions`**

| 属性 | 默认值 | 说明 |
|---|---|---|
| `SpinThreshold` | `1.5 ms` | 距目标时刻此值以内切换为忙等待 |
| `ChannelCapacity` | `1` | 有界 channel 容量 |
| `FullMode` | `DropOldest` | channel 满时的处理策略（慢消费者场景） |
| `ThreadPriority` | `AboveNormal` | 专用 ticker 线程的优先级 |
| `ThreadName` | 自动 | 在调试器 / 分析器中可见的线程名称 |

## 慢消费者与背压

计时线程调用 `TryWrite` 且永不阻塞。channel 满时：

- **`DropOldest`**（默认）— 丢弃最旧的待处理 tick，消费者始终获得最新事件。`Sequence` 出现间隙即可得知丢弃了多少 tick。
- **`DropNewest`** — 丢弃新到达的 tick，消费者按自身速度消费。
- **`Wait`** — 阻塞计时线程（不建议在精度敏感场景中使用）。

## 广播给多个消费者

`ChannelReader<T>` 每个事件只投递给一个读取者。如需广播语义，可手动分发：

```csharp
using var ticker = new GhostTicker(TimeSpan.FromMilliseconds(100));

var ch1 = Channel.CreateBounded<TimerEvent>(4);
var ch2 = Channel.CreateBounded<TimerEvent>(4);

// 分发器
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

// 消费者 A
_ = Task.Run(async () =>
{
    await foreach (var evt in ch1.Reader.ReadAllAsync())
        Console.WriteLine($"A #{evt.Sequence}");
});
```

## 基准测试

> 环境：Windows 11，13th Gen Intel Core i9-13980HX，.NET 10.0.6，BenchmarkDotNet v0.15.8

### 触发精度 — 相对计划时刻的误差（µs，越低越好）

每格 100 个样本。GhostTicker 使用 `timeBeginPeriod(1)` + 忙等待；其他方案依赖默认 OS 计时器（Windows 上约 15 ms 粒度）。

| 方法 | 延迟 | 最小值 | 均值 | 标准差 | P95 | P99 |
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

在 1 ms 延迟下，GhostTicker 均值误差为 **0.3 µs**，其他方案约为 **14,600 µs** — 提升约 **49,000×**。

### Tick 节奏 — 100 次连续 tick 的总耗时（ms，越低越好）

GhostTicker 使用独立的 `AboveNormal` 优先级线程；其他方案依赖线程池。

| 方法 | 间隔 | 均值 | 比率 | 内存分配 |
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

本地运行基准测试：
```
dotnet run --project benchmarks/GhostTick.Benchmarks -c Release
```

## 示例

可运行示例请参见 [`examples/GhostTick.Examples`](examples/GhostTick.Examples)，涵盖所有功能。

```
dotnet run --project examples/GhostTick.Examples
```

## 许可证

MIT
