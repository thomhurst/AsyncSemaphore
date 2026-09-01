using AsyncSemaphore.Benchmark;
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromTypes([typeof(Benchmarks), typeof(PoolComparisonBenchmarks)]).Run(args);
