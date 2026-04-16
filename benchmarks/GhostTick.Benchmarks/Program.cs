using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using GhostTick.Benchmarks;

// ── Precision (custom sampler, measures actual fire error) ───────────────────
await PrecisionBenchmark.RunAsync();

// ── Jitter + Overhead (BenchmarkDotNet) ──────────────────────────────────────
// Usage:
//   dotnet run -c Release                          — run all BDN benchmarks
//   dotnet run -c Release -- --filter *Jitter*     — jitter only
//   dotnet run -c Release -- --filter *Overhead*   — overhead only
//
// BenchmarkDotNet requires Release configuration.
var artifactsPath = Path.Combine(
    AppContext.BaseDirectory, // artifacts/bin/GhostTick.Benchmarks/release/
    "..", "..", "..", "..", "..", // up to repo root
    "artifacts", "BenchmarkDotNet.Artifacts");

var config = DefaultConfig.Instance
    .WithArtifactsPath(Path.GetFullPath(artifactsPath));

BenchmarkSwitcher
    .FromTypes([typeof(JitterBenchmark)])
    .Run(args, config);