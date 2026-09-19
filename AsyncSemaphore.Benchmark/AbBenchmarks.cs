using AsyncSemaphore.Benchmark.Baseline;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AsyncSemaphore.Benchmark;

/// <summary>
/// Same-run A/B of a frozen snapshot of the core (<see cref="BaselineAsyncSemaphore"/>, commit 575a0b2)
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

    private const int Gates = 2;

    private const int CountedPermits = 4;

    private static readonly int FanOutWorkers = Environment.ProcessorCount;

    private readonly BaselineAsyncSemaphore _old = new(1);
    private readonly Semaphores.AsyncSemaphore _new = new(1);
    private readonly CancellationTokenSource _cts = new();

    // More than one permit: these handles always release through a state object of their own.
    private readonly BaselineAsyncSemaphore _oldCounted = new(CountedPermits);
    private readonly Semaphores.AsyncSemaphore _newCounted = new(CountedPermits);

    private readonly BaselineAsyncSemaphore[] _oldGates = [.. Enumerable.Range(0, Gates).Select(_ => new BaselineAsyncSemaphore(1))];
    private readonly Semaphores.AsyncSemaphore[] _newGates = [.. Enumerable.Range(0, Gates).Select(_ => new Semaphores.AsyncSemaphore(1))];

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Construct")]
    public BaselineAsyncSemaphore Old_Construct() => new(1);

    [Benchmark]
    [BenchmarkCategory("Construct")]
    public Semaphores.AsyncSemaphore New_Construct() => new(1);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ConstructFirstContention")]
    public async Task Old_ConstructFirstContention()
    {
        // A fresh gate per invoke with a single contended wait, so any first-contention setup cost
        // is paid inside the measurement instead of being amortised away.
        var semaphore = new BaselineAsyncSemaphore(1);
        var holder = await semaphore.WaitAsync();
        var pending = semaphore.WaitAsync();
        holder.Dispose();
        holder = await pending;
        holder.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("ConstructFirstContention")]
    public async Task New_ConstructFirstContention()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);
        var holder = await semaphore.WaitAsync();
        var pending = semaphore.WaitAsync();
        holder.Dispose();
        holder = await pending;
        holder.Dispose();
    }

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
    [BenchmarkCategory("CountedUncontended")]
    public async Task Old_CountedUncontended()
    {
        using var _ = await _oldCounted.WaitAsync();
    }

    [Benchmark]
    [BenchmarkCategory("CountedUncontended")]
    public async Task New_CountedUncontended()
    {
        using var _ = await _newCounted.WaitAsync();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("TryWait")]
    public bool Old_TryWait()
    {
        if (!_old.TryWait(out var releaser))
        {
            return false;
        }

        releaser.Dispose();

        return true;
    }

    [Benchmark]
    [BenchmarkCategory("TryWait")]
    public bool New_TryWait()
    {
        if (!_new.TryWait(out var releaser))
        {
            return false;
        }

        releaser.Dispose();

        return true;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("SyncUncontended")]
    public void Old_SyncUncontended()
    {
        using var _ = _old.Wait();
    }

    [Benchmark]
    [BenchmarkCategory("SyncUncontended")]
    public void New_SyncUncontended()
    {
        using var _ = _new.Wait();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("SyncParallel")]
    public Task Old_SyncParallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                using var @lock = _old.Wait();
                Thread.Yield();
            }
        })));
    }

    [Benchmark(OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("SyncParallel")]
    public Task New_SyncParallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                using var @lock = _new.Wait();
                Thread.Yield();
            }
        })));
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

    // Issue #589: every logical core blocks on an async method that takes the gate twice. A fresh
    // gate per invoke pays its first-contention setup inside the measurement; the warm pair below
    // reuses one gate, which leaves only the steady-state handoff.
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FreshGateParallel")]
    public void Old_FreshGateParallel()
    {
        var gate = new BaselineAsyncSemaphore(1);

        Parallel.For(0, FanOutWorkers, _ => TakeTwice(gate).AsTask().GetAwaiter().GetResult());
    }

    [Benchmark]
    [BenchmarkCategory("FreshGateParallel")]
    public void New_FreshGateParallel()
    {
        var gate = new Semaphores.AsyncSemaphore(1);

        Parallel.For(0, FanOutWorkers, _ => TakeTwice(gate).AsTask().GetAwaiter().GetResult());
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("WarmGateParallel")]
    public void Old_WarmGateParallel()
    {
        Parallel.For(0, FanOutWorkers, _ => TakeTwice(_old).AsTask().GetAwaiter().GetResult());
    }

    [Benchmark]
    [BenchmarkCategory("WarmGateParallel")]
    public void New_WarmGateParallel()
    {
        Parallel.For(0, FanOutWorkers, _ => TakeTwice(_new).AsTask().GetAwaiter().GetResult());
    }

    // The same fan-out split over independent gates, so overflow traffic from more than one semaphore
    // meets in the shared pool: where it has the most to lose against one pool per instance.
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("MultiGateParallel")]
    public void Old_MultiGateParallel()
    {
        Parallel.For(0, FanOutWorkers, worker => TakeTwice(_oldGates[worker % Gates]).AsTask().GetAwaiter().GetResult());
    }

    [Benchmark]
    [BenchmarkCategory("MultiGateParallel")]
    public void New_MultiGateParallel()
    {
        Parallel.For(0, FanOutWorkers, worker => TakeTwice(_newGates[worker % Gates]).AsTask().GetAwaiter().GetResult());
    }

    private static async ValueTask TakeTwice(BaselineAsyncSemaphore gate)
    {
        for (var i = 0; i < 2; i++)
        {
            using (await gate.WaitAsync().ConfigureAwait(false))
            {
            }
        }
    }

    private static async ValueTask TakeTwice(Semaphores.AsyncSemaphore gate)
    {
        for (var i = 0; i < 2; i++)
        {
            using (await gate.WaitAsync().ConfigureAwait(false))
            {
            }
        }
    }
}
