using System.Runtime.CompilerServices;

namespace Semaphores;

public struct AsyncSemaphoreReleaser : IDisposable
{
    private AsyncSemaphore? _semaphore;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal AsyncSemaphoreReleaser(AsyncSemaphore semaphore)
    {
        _semaphore = semaphore;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
