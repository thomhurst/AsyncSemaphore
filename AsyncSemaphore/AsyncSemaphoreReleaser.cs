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
        // A plain read-then-clear keeps a repeated Dispose of the same struct a no-op without paying for
        // an interlocked exchange on every release; the interlocked publish is Release() itself.
        var semaphore = _semaphore;

        if (semaphore is not null)
        {
            _semaphore = null;
            semaphore.Release();
        }
    }
}
