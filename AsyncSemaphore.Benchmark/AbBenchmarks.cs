using AsyncSemaphore.Benchmark.Baseline;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AsyncSemaphore.Benchmark;

/// <summary>
/// Same-run A/B of a frozen snapshot of the core (<see cref="BaselineAsyncSemaphore"/>, commit 66ad5f7)
/// against the working-tree core, so a change can be measured without cross-run noise.
/// Run with <c>--filter "*AbBenchmarks*"</c>.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AbBenchmarks
{
    private const int HandoffOperations = 1_000;
    private const int ParallelWorkers = 4;
    private const int ParallelOperationsPerWorker = 250;

    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(5);

    private readonly BaselineAsyncSemaphore _old = new(1);
    private readonly Semaphores.AsyncSemaphore _new = new(1);
    private readonly CancellationTokenSource _cts = new();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Uncontended")]
    public async Task Old_Uncontended()
    {
        using var _ = await _old.WaitAsync();
    }

    [Benchmark]
    [BenchmarkCategory("Uncontended")]
    public async Task New_Uncontended()
    {
        using var _ = await _new.WaitAsync();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("TimeoutUncontended")]
    public async Task Old_TimeoutUncontended()
    {
        using var _ = await _old.WaitAsync(LongTimeout);
    }

    [Benchmark]
    [BenchmarkCategory("TimeoutUncontended")]
    public async Task New_TimeoutUncontended()
    {
        using var _ = await _new.WaitAsync(LongTimeout);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("TokenUncontended")]
    public async Task Old_TokenUncontended()
    {
        using var _ = await _old.WaitAsync(_cts.Token);
    }

    [Benchmark]
    [BenchmarkCategory("TokenUncontended")]
    public async Task New_TokenUncontended()
    {
        using var _ = await _new.WaitAsync(_cts.Token);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("AsyncHandoff")]
    public async Task Old_AsyncHandoff()
    {
        var holder = await _old.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _old.WaitAsync();
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("AsyncHandoff")]
    public async Task New_AsyncHandoff()
    {
        var holder = await _new.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _new.WaitAsync();
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("TimeoutHandoff")]
    public async Task Old_TimeoutHandoff()
    {
        var holder = await _old.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _old.WaitAsync(LongTimeout);
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("TimeoutHandoff")]
    public async Task New_TimeoutHandoff()
    {
        var holder = await _new.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _new.WaitAsync(LongTimeout);
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("TokenHandoff")]
    public async Task Old_TokenHandoff()
    {
        var holder = await _old.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _old.WaitAsync(_cts.Token);
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("TokenHandoff")]
    public async Task New_TokenHandoff()
    {
        var holder = await _new.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _new.WaitAsync(_cts.Token);
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("Parallel")]
    public Task Old_Parallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(async _ =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                using var @lock = await _old.WaitAsync();
                await Task.Yield();
            }
        }));
    }

    [Benchmark(OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("Parallel")]
    public Task New_Parallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(async _ =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                using var @lock = await _new.WaitAsync();
                await Task.Yield();
            }
        }));
    }
}
