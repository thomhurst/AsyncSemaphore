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

    // Signal-style gates: the count starts at zero and permits are published by code that never waited.
    private readonly SemaphoreSlim _signalSlim = new(0);
    private readonly Semaphores.UnpairedAsyncSemaphore _unpairedSemaphore = new(0);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Construct")]
    public SemaphoreSlim SemaphoreSlim_Construct() => new(1, 1);

    [Benchmark]
    [BenchmarkCategory("Construct")]
    public Semaphores.AsyncSemaphore AsyncSemaphore_Construct() => new(1);

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

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("TryWait")]
    public bool SemaphoreSlim_TryWait()
    {
        if (!_semaphoreSlim.Wait(0))
        {
            return false;
        }

        _semaphoreSlim.Release();

        return true;
    }

    [Benchmark]
    [BenchmarkCategory("TryWait")]
    public bool AsyncSemaphore_TryWait()
    {
        if (!_asyncSemaphore.TryWait(out var releaser))
        {
            return false;
        }

        releaser.Dispose();

        return true;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("SyncUncontended")]
    public void SemaphoreSlim_Sync()
    {
        _semaphoreSlim.Wait();
        _semaphoreSlim.Release();
    }

    [Benchmark]
    [BenchmarkCategory("SyncUncontended")]
    public void AsyncSemaphore_Sync()
    {
        using var _ = _asyncSemaphore.Wait();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("SyncParallel")]
    public Task SemaphoreSlim_SyncParallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                _semaphoreSlim.Wait();

                try
                {
                    Thread.Yield();
                }
                finally
                {
                    _semaphoreSlim.Release();
                }
            }
        })));
    }

    [Benchmark(OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("SyncParallel")]
    public Task AsyncSemaphore_SyncParallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                using var @lock = _asyncSemaphore.Wait();
                Thread.Yield();
            }
        })));
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("UnpairedUncontended")]
    public async Task SemaphoreSlim_Unpaired()
    {
        _signalSlim.Release();
        await _signalSlim.WaitAsync();
    }

    [Benchmark]
    [BenchmarkCategory("UnpairedUncontended")]
    public async Task UnpairedAsyncSemaphore()
    {
        _unpairedSemaphore.Release();
        await _unpairedSemaphore.WaitAsync();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("UnpairedSignal")]
    public async Task SemaphoreSlim_UnpairedSignal()
    {
        // A waiter queues on an empty gate and is woken by a release that never waited.
        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _signalSlim.WaitAsync();
            _signalSlim.Release();
            await pending;
        }
    }

    [Benchmark(OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("UnpairedSignal")]
    public async Task UnpairedAsyncSemaphore_UnpairedSignal()
    {
        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _unpairedSemaphore.WaitAsync();
            _unpairedSemaphore.Release();
            await pending;
        }
    }
}
