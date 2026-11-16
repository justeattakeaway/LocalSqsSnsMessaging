// Request-path benchmarks for the library and the server. Run from the repository root:
//
//   dotnet run -c Release --project benchmarks/LocalSqsSnsMessaging.Benchmarks -- --filter '*'
//
// Compare allocated bytes per operation (exact) and mean time (within its error) between two
// commits. Benchmarks run in-process, so no separate benchmark project is generated and built.

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using LocalSqsSnsMessaging.Benchmarks;

var config = DefaultConfig.Instance
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(5).WithIterationCount(20));

BenchmarkSwitcher.FromAssembly(typeof(WireBenchmarks).Assembly).Run(args, config);
