#pragma warning disable SEM0001

namespace Semaphores;

public sealed class AsyncSemaphore : IAsyncSemaphore
{
    private readonly SemaphoreSlim _semaphoreSlim;

    private readonly AsyncSemaphoreLock _lock;

    public AsyncSemaphore(int maxCount)
    {
        _semaphoreSlim = new(maxCount, maxCount);
        _lock = new AsyncSemaphoreLock(_semaphoreSlim);
    }

    /// <inheritdoc />
    public async ValueTask<IDisposable> WaitAsync()
    {
        await _semaphoreSlim.WaitAsync();
        return _lock;
    }
    
    /// <inheritdoc />
    public async ValueTask<IDisposable> WaitAsync(TimeSpan timeout)
    {
        await _semaphoreSlim.WaitAsync(timeout);
        return _lock;
    }
    
    /// <inheritdoc />
    public async ValueTask<IDisposable> WaitAsync(CancellationToken cancellationToken)
    {
        await _semaphoreSlim.WaitAsync(cancellationToken);
        return _lock;
    }
    
    /// <inheritdoc />
    public async ValueTask<IDisposable> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _semaphoreSlim.WaitAsync(timeout, cancellationToken);
        return _lock;
    }

    /// <inheritdoc />
    public int CurrentCount => _semaphoreSlim.CurrentCount;

    /// <inheritdoc />
    public void Dispose()
    {
        _semaphoreSlim.Dispose();
    }
}