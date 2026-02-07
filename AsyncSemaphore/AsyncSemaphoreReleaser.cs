using System.Runtime.CompilerServices;

namespace Semaphores;

public struct AsyncSemaphoreReleaser : IDisposable
{
    private SemaphoreSlim? _semaphoreSlim;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal AsyncSemaphoreReleaser(SemaphoreSlim semaphoreSlim)
    {
        _semaphoreSlim = semaphoreSlim;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        Interlocked.Exchange(ref _semaphoreSlim, null)?.Release();
    }
}