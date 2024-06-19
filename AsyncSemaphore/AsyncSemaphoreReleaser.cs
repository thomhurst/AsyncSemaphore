namespace Semaphores;

public struct AsyncSemaphoreReleaser : IDisposable
{
    private SemaphoreSlim? _semaphoreSlim;

    internal AsyncSemaphoreReleaser(SemaphoreSlim semaphoreSlim)
    {
        _semaphoreSlim = semaphoreSlim;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _semaphoreSlim, null)?.Release();
    }
}