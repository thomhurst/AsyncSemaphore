using System.Runtime.CompilerServices;

namespace AsyncSemaphore.Benchmark.Baseline;

public struct BaselineReleaser : IDisposable
{
    private BaselineAsyncSemaphore? _semaphore;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal BaselineReleaser(BaselineAsyncSemaphore semaphore)
    {
        _semaphore = semaphore;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
