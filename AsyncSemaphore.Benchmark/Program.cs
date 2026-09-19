using AsyncSemaphore.Benchmark;
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromTypes([typeof(Benchmarks), typeof(PoolComparisonBenchmarks), typeof(AbBenchmarks), typeof(FreshGateBenchmarks), typeof(PairedAcquisitionBenchmarks)]).Run(args);
