using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AsyncSemaphore.Benchmark;

/// <summary>
/// Models the integration workload from issue #589: every logical core blocks on an async method
/// that takes a gate twice around a short critical section. <c>FreshGateParallel</c> builds the gate
/// inside the measurement, so first-contention setup is paid on every invoke;
/// <c>WarmGateParallel</c> reuses one gate, which leaves only the steady-state parallel handoff.
/// <c>WorkBetweenParallel</c> puts work that needs no gate between the two acquisitions, as the
/// integration has it: a waiter resumed on the thread pool goes on to that work, so how long the gate
/// stays idle between a release and the next critical section decides the result.
/// Run with <c>--filter "*FreshGateBenchmarks*"</c>.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class FreshGateBenchmarks
{
    private static readonly int Workers = Environment.ProcessorCount;

    private readonly SemaphoreSlim _warmSlim = new(1, 1);
    private readonly Semaphores.AsyncSemaphore _warmPaired = new(1);
    private readonly Semaphores.UnpairedAsyncSemaphore _warmUnpaired = new(1);

    private readonly object _sync = new();
    private int _entries;

    /// <summary>The floor: the same fan-out and blocking with no gate at all.</summary>
    [Benchmark]
    [BenchmarkCategory("ParallelForControl")]
    public int NoGate_Parallel()
    {
        Parallel.For(0, Workers, _ => NoGateAsync().AsTask().GetAwaiter().GetResult());

        return _entries;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FreshGateParallel")]
    public int SemaphoreSlim_FreshGateParallel()
    {
        var gate = new SemaphoreSlim(1, 1);

        Parallel.For(0, Workers, _ => SlimAsync(gate).AsTask().GetAwaiter().GetResult());

        return _entries;
    }

    [Benchmark]
    [BenchmarkCategory("FreshGateParallel")]
    public int AsyncSemaphore_FreshGateParallel()
    {
        var gate = new Semaphores.AsyncSemaphore(1);

        Parallel.For(0, Workers, _ => PairedAsync(gate).AsTask().GetAwaiter().GetResult());

        return _entries;
    }

    [Benchmark]
    [BenchmarkCategory("FreshGateParallel")]
    public int Unpaired_FreshGateParallel()
    {
        var gate = new Semaphores.UnpairedAsyncSemaphore(1);

        Parallel.For(0, Workers, _ => UnpairedAsync(gate).AsTask().GetAwaiter().GetResult());

        return _entries;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("WarmGateParallel")]
    public int SemaphoreSlim_WarmGateParallel()
    {
        Parallel.For(0, Workers, _ => SlimAsync(_warmSlim).AsTask().GetAwaiter().GetResult());

        return _entries;
    }

    [Benchmark]
    [BenchmarkCategory("WarmGateParallel")]
    public int AsyncSemaphore_WarmGateParallel()
    {
        Parallel.For(0, Workers, _ => PairedAsync(_warmPaired).AsTask().GetAwaiter().GetResult());

        return _entries;
    }

    [Benchmark]
    [BenchmarkCategory("WarmGateParallel")]
    public int Unpaired_WarmGateParallel()
    {
        Parallel.For(0, Workers, _ => UnpairedAsync(_warmUnpaired).AsTask().GetAwaiter().GetResult());

        return _entries;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("WorkBetweenParallel")]
    public int SemaphoreSlim_WorkBetweenParallel()
    {
        Parallel.For(0, Workers, worker => SlimWorkBetweenAsync(_warmSlim, worker).AsTask().GetAwaiter().GetResult());

        return _entries;
    }

    [Benchmark]
    [BenchmarkCategory("WorkBetweenParallel")]
    public int AsyncSemaphore_WorkBetweenParallel()
    {
        Parallel.For(0, Workers, worker => PairedWorkBetweenAsync(_warmPaired, worker).AsTask().GetAwaiter().GetResult());

        return _entries;
    }

    private async ValueTask<int> NoGateAsync()
    {
        Mutate();
        await default(ValueTask).ConfigureAwait(false);
        Mutate();

        return _entries;
    }

    private async ValueTask<int> SlimAsync(SemaphoreSlim gate)
    {
        for (var i = 0; i < 2; i++)
        {
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                Mutate();
            }
            finally
            {
                gate.Release();
            }
        }

        return _entries;
    }

    private async ValueTask<int> PairedAsync(Semaphores.AsyncSemaphore gate)
    {
        for (var i = 0; i < 2; i++)
        {
            using (await gate.WaitAsync().ConfigureAwait(false))
            {
                Mutate();
            }
        }

        return _entries;
    }

    private async ValueTask<int> UnpairedAsync(Semaphores.UnpairedAsyncSemaphore gate)
    {
        for (var i = 0; i < 2; i++)
        {
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                Mutate();
            }
            finally
            {
                gate.Release();
            }
        }

        return _entries;
    }

    private async ValueTask<int> SlimWorkBetweenAsync(SemaphoreSlim gate, int seed)
    {
        await gate.WaitAsync().ConfigureAwait(false);

        try
        {
            Mutate();
        }
        finally
        {
            gate.Release();
        }

        var result = WorkOutsideTheGate(seed);

        await gate.WaitAsync().ConfigureAwait(false);

        try
        {
            Mutate();
        }
        finally
        {
            gate.Release();
        }

        return result;
    }

    private async ValueTask<int> PairedWorkBetweenAsync(Semaphores.AsyncSemaphore gate, int seed)
    {
        using (await gate.WaitAsync().ConfigureAwait(false))
        {
            Mutate();
        }

        var result = WorkOutsideTheGate(seed);

        using (await gate.WaitAsync().ConfigureAwait(false))
        {
            Mutate();
        }

        return result;
    }

    /// <summary>Stands in for building the value the integration publishes: about half a microsecond that needs no gate.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int WorkOutsideTheGate(int seed)
    {
        var value = seed;

        for (var i = 0; i < 1_024; i++)
        {
            value = (value * 31) + i;
        }

        return value;
    }

    /// <summary>Stands in for the dictionary bookkeeping the integration does under its gate.</summary>
    private void Mutate()
    {
        lock (_sync)
        {
            _entries++;
        }
    }
}
