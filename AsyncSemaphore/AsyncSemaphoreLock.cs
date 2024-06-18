namespace Semaphores;

internal sealed class AsyncSemaphoreLock(SemaphoreSlim semaphoreSlim) : IDisposable
{
    public void Dispose()
    {
        semaphoreSlim.Release();
    }
}