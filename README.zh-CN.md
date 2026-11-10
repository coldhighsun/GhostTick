# GhostTick

[![CI](https://github.com/coldhighsun/GhostTick/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/GhostTick/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/GhostTick)](https://www.nuget.org/packages/GhostTick)
[![NuGet Downloads](https://img.shields.io/nuget/dt/GhostTick)](https://www.nuget.org/packages/GhostTick)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[English](README.md) | **简体中文**

适用于 .NET 的高精度计时工具库，通过 `System.Threading.Channels` 传递事件。

## 功能特性

- **亚毫秒级精度** — 采用休眠 + 忙等待混合策略；将 Windows 的 OS 定时器粒度从约 15 ms 降至约 1 ms
- **基于 Channel 的 API** — 消费者通过 `ChannelReader<TimerEvent>` 接收 `TimerEvent`，计时线程与消费者延迟完全解耦
- **漂移修正** — `GhostTicker` 将每个目标时刻计算为 `start + seq × interval`，无论运行多久，累积误差始终有界
- **目标框架** — `net10.0`

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
- **`Wait`** — 对计时线程无效；始终使用 `TryWrite`，channel 满时 tick 仍会被丢弃。

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

> 环境：Windows 11，13th Gen Intel Core i9-13980HX，.NET 10.0.14，BenchmarkDotNet v0.15.8

### 触发精度 — 相对计划时刻的误差（µs，越低越好）

每格 100 个样本。

| 方法 | 延迟 | 最小值 | 均值 | 标准差 | P95 | P99 |
|---|---:|---:|---:|---:|---:|---:|
| **GhostTicker** | 1 ms | **0.0** | **0.3** | **0.5** | **0.5** | **0.7** |
| Task.Delay | 1 ms | 13,598 | 14,421 | 375 | 15,052 | 15,129 |
| Threading.Timer | 1 ms | 13,647 | 14,384 | 407 | 15,072 | 15,209 |
| Timers.Timer | 1 ms | 13,636 | 14,428 | 377 | 15,059 | 15,146 |
| **GhostTicker** | 5 ms | **0.1** | **0.7** | **1.4** | **0.9** | **8.4** |
| Task.Delay | 5 ms | 9,646 | 10,369 | 437 | 11,190 | 11,438 |
| Threading.Timer | 5 ms | 9,717 | 10,459 | 453 | 11,079 | 11,680 |
| Timers.Timer | 5 ms | 9,533 | 10,376 | 425 | 11,103 | 11,148 |
| **GhostTicker** | 10 ms | **0.3** | **2.3** | **10.2** | **4.1** | **42.4** |
| Task.Delay | 10 ms | 4,542 | 5,455 | 430 | 6,061 | 6,675 |
| Threading.Timer | 10 ms | 4,594 | 5,417 | 436 | 6,106 | 6,388 |
| Timers.Timer | 10 ms | 4,744 | 5,409 | 427 | 6,039 | 6,167 |

在 1 ms 延迟下，GhostTicker 均值误差为 **0.3 µs**，其他方案约为 **14,400 µs** — 提升约 **48,000×**。

### Tick 节奏 — 100 次连续 tick 的总耗时（ms，越低越好）

GhostTicker 使用独立的 `AboveNormal` 优先级线程；其他方案依赖线程池。

| 方法 | 间隔 | 均值 | 比率 | 内存分配 |
|---|---:|---:|---:|---:|
| **GhostTicker** | 1 ms | **100.1 ms** | 1.00 | 16.64 KB |
| PeriodicTimer | 1 ms | 1,542.9 ms | 15.42× | 2.32 KB |
| Threading.Timer | 1 ms | 1,542.0 ms | 15.41× | 2.80 KB |
| **GhostTicker** | 10 ms | **1,000.1 ms** | 1.00 | 16.87 KB |
| PeriodicTimer | 10 ms | 1,544.4 ms | 1.54× | 14.37 KB |
| Threading.Timer | 10 ms | 1,539.8 ms | 1.54× | 2.83 KB |
| **GhostTicker** | 50 ms | 5,000.1 ms | 1.00 | 17.03 KB |
| PeriodicTimer | 50 ms | 5,008.2 ms | 1.00× | 2.32 KB |
| Threading.Timer | 50 ms | 5,006.5 ms | 1.00× | 2.80 KB |

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
