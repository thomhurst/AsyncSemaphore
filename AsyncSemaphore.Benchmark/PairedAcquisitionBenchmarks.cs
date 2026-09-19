using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AsyncSemaphore.Benchmark;

/// <summary>
/// Issue #590: what a paired acquisition allocates next to the <see cref="SemaphoreSlim"/> region it
/// replaces. The methods return <see cref="ValueTask"/> so that the only allocation left to measure is
/// the semaphore's own. An exclusive gate (one permit) and a counted gate (several) are kept apart
/// because only the first can release through its own epoch instead of an allocated state. Run with <c>--filter "*PairedAcquisitionBenchmarks*"</c>.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PairedAcquisitionBenchmarks
{
    private const int CountedPermits = 4;
    private const int HandoffOperations = 1_000;

    private readonly SemaphoreSlim _slim = new(1, 1);
    private readonly Semaphores.AsyncSemaphore _paired = new(1);

    private readonly SemaphoreSlim _countedSlim = new(CountedPermits, CountedPermits);
    private readonly Semaphores.AsyncSemaphore _countedPaired = new(CountedPermits);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Exclusive")]
    public async ValueTask Slim_Exclusive()
    {
        await _slim.WaitAsync().ConfigureAwait(false);

        try
        {
        }
        finally
        {
            _slim.Release();
        }
    }

    [Benchmark]
    [BenchmarkCategory("Exclusive")]
    public async ValueTask Paired_Exclusive()
    {
        using (await _paired.WaitAsync().ConfigureAwait(false))
        {
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Counted")]
    public async ValueTask Slim_Counted()
    {
        await _countedSlim.WaitAsync().ConfigureAwait(false);

        try
        {
        }
        finally
        {
            _countedSlim.Release();
        }
    }

    [Benchmark]
    [BenchmarkCategory("Counted")]
    public async ValueTask Paired_Counted()
    {
        using (await _countedPaired.WaitAsync().ConfigureAwait(false))
        {
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("ExclusiveHandoff")]
    public async Task Slim_ExclusiveHandoff()
    {
        await _slim.WaitAsync().ConfigureAwait(false);

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _slim.WaitAsync();
            _slim.Release();
            await pending.ConfigureAwait(false);
        }

        _slim.Release();
    }

    // A queued wait gets its handle from the releasing thread, not from the fast path.
    [Benchmark(OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("ExclusiveHandoff")]
    public async Task Paired_ExclusiveHandoff()
    {
        var holder = await _paired.WaitAsync().ConfigureAwait(false);

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _paired.WaitAsync();
            holder.Dispose();
            holder = await pending.ConfigureAwait(false);
        }

        holder.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ExclusiveSync")]
    public void Slim_ExclusiveSync()
    {
        _slim.Wait();

        try
        {
        }
        finally
        {
            _slim.Release();
        }
    }

    [Benchmark]
    [BenchmarkCategory("ExclusiveSync")]
    public void Paired_ExclusiveSync()
    {
        using (_paired.Wait())
        {
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _slim.Dispose();
        _paired.Dispose();
        _countedSlim.Dispose();
        _countedPaired.Dispose();
    }
}
