namespace Semaphores;

internal class AsyncSemaphoreLock(SemaphoreSlim semaphoreSlim) : IDisposable
{
    public void Dispose()
    {
        semaphoreSlim.Release();
    }
}