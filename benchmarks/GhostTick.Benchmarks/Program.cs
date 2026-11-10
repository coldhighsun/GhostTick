using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using GhostTick.Benchmarks;

// ── Benchmarks (BenchmarkDotNet) ──────────────────────────────────
// Usage:
//   dotnet run -c Release                              — run all benchmarks
//   dotnet run -c Release -- --filter *Jitter*         — jitter only
//   dotnet run -c Release -- --filter *Precision*      — precision (fire error) only
//
// BenchmarkDotNet requires Release configuration.
// Locate the repository root (the folder containing GhostTick.slnx) so reports always land in
// <repo>/artifacts/BenchmarkDotNet.Artifacts regardless of the build output layout.
var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "GhostTick.slnx")))
{
    repoRoot = repoRoot.Parent;
}

var artifactsPath = Path.Combine(
    repoRoot?.FullName ?? Directory.GetCurrentDirectory(),
    "artifacts", "BenchmarkDotNet.Artifacts");

var config = DefaultConfig.Instance
    .WithArtifactsPath(artifactsPath);

// Without arguments the switcher would prompt interactively; default to running everything.
BenchmarkSwitcher
    .FromTypes([typeof(PrecisionBenchmark), typeof(JitterBenchmark)])
    .Run(args.Length == 0 ? ["--filter", "*"] : args, config);
