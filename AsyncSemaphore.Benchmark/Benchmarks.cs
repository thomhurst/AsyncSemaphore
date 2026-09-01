using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AsyncSemaphore.Benchmark;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class Benchmarks
{
    private const int HandoffOperations = 1_000;
    private const int ParallelWorkers = 4;
    private const int ParallelOperationsPerWorker = 250;

    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);
    private readonly Semaphores.AsyncSemaphore _asyncSemaphore = new(1);
    private readonly CancellationTokenSource _cts = new();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Uncontended")]
    public async Task SemaphoreSlim()
    {
        try
        {
            await _semaphoreSlim.WaitAsync();
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    [Benchmark]
    [BenchmarkCategory("Uncontended")]
    public async Task AsyncSemaphore()
    {
        using var _ = await _asyncSemaphore.WaitAsync();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("AsyncHandoff")]
    public async Task SemaphoreSlim_AsyncHandoff()
    {
        await _semaphoreSlim.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _semaphoreSlim.WaitAsync();
            _semaphoreSlim.Release();
            await pending;
        }

        _semaphoreSlim.Release();
    }

    [Benchmark(OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("AsyncHandoff")]
    public async Task AsyncSemaphore_AsyncHandoff()
    {
        var holder = await _asyncSemaphore.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _asyncSemaphore.WaitAsync();
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("TimeoutUncontended")]
    public async Task SemaphoreSlim_Timeout()
    {
        try
        {
            await _semaphoreSlim.WaitAsync(LongTimeout);
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    [Benchmark]
    [BenchmarkCategory("TimeoutUncontended")]
    public async Task AsyncSemaphore_Timeout()
    {
        using var _ = await _asyncSemaphore.WaitAsync(LongTimeout);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("TokenUncontended")]
    public async Task SemaphoreSlim_Token()
    {
        try
        {
            await _semaphoreSlim.WaitAsync(_cts.Token);
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    [Benchmark]
    [BenchmarkCategory("TokenUncontended")]
    public async Task AsyncSemaphore_Token()
    {
        using var _ = await _asyncSemaphore.WaitAsync(_cts.Token);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("TimeoutHandoff")]
    public async Task SemaphoreSlim_TimeoutHandoff()
    {
        await _semaphoreSlim.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _semaphoreSlim.WaitAsync(LongTimeout);
            _semaphoreSlim.Release();
            await pending;
        }

        _semaphoreSlim.Release();
    }

    [Benchmark(OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("TimeoutHandoff")]
    public async Task AsyncSemaphore_TimeoutHandoff()
    {
        var holder = await _asyncSemaphore.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _asyncSemaphore.WaitAsync(LongTimeout);
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("TokenHandoff")]
    public async Task SemaphoreSlim_TokenHandoff()
    {
        await _semaphoreSlim.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _semaphoreSlim.WaitAsync(_cts.Token);
            _semaphoreSlim.Release();
            await pending;
        }

        _semaphoreSlim.Release();
    }

    [Benchmark(OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("TokenHandoff")]
    public async Task AsyncSemaphore_TokenHandoff()
    {
        var holder = await _asyncSemaphore.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _asyncSemaphore.WaitAsync(_cts.Token);
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("Parallel")]
    public Task SemaphoreSlim_Parallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(async _ =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                await _semaphoreSlim.WaitAsync();

                try
                {
                    await Task.Yield();
                }
                finally
                {
                    _semaphoreSlim.Release();
                }
            }
        }));
    }

    [Benchmark(OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("Parallel")]
    public Task AsyncSemaphore_Parallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(async _ =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                using var @lock = await _asyncSemaphore.WaitAsync();
                await Task.Yield();
            }
        }));
    }
}
