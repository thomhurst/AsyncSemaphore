using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AsyncSemaphore.Benchmark;

/// <summary>
/// Models the integration workload from issue #589: every logical core blocks on an async method
/// that takes a gate twice around a short critical section. <c>FreshGateParallel</c> builds the gate
/// inside the measurement, so first-contention setup is paid on every invoke;
/// <c>WarmGateParallel</c> reuses one gate, which leaves only the steady-state parallel handoff.
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

    /// <summary>Stands in for the dictionary bookkeeping the integration does under its gate.</summary>
    private void Mutate()
    {
        lock (_sync)
        {
            _entries++;
        }
    }
}
