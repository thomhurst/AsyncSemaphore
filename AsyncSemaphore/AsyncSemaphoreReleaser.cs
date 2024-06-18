namespace Semaphores;

public sealed class AsyncSemaphoreReleaser : IDisposable
{
    private readonly SemaphoreSlim _semaphoreSlim;

    internal AsyncSemaphoreReleaser(SemaphoreSlim semaphoreSlim)
    {
        _semaphoreSlim = semaphoreSlim;
    }

    public void Dispose()
    {
        _semaphoreSlim.Release();
    }
}