using AsyncSemaphore.Benchmark;
using BenchmarkDotNet.Running;

var summary = BenchmarkRunner.Run<Benchmarks>();
