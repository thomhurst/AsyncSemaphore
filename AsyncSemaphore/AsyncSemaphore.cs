#pragma warning disable SEM0001

namespace Semaphores;

public sealed class AsyncSemaphore : IAsyncSemaphore
{
    private readonly SemaphoreSlim _semaphoreSlim;

    private readonly AsyncSemaphoreReleaser _releaser;

    public AsyncSemaphore(int maxCount)
    {
        _semaphoreSlim = new(maxCount, maxCount);
        _releaser = new AsyncSemaphoreReleaser(_semaphoreSlim);
    }

    /// <inheritdoc />
    public async ValueTask<AsyncSemaphoreReleaser> WaitAsync()
    {
        await _semaphoreSlim.WaitAsync();
        return _releaser;
    }
    
    /// <inheritdoc />
    public async ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout)
    {
        await _semaphoreSlim.WaitAsync(timeout);
        return _releaser;
    }
    
    /// <inheritdoc />
    public async ValueTask<AsyncSemaphoreReleaser> WaitAsync(CancellationToken cancellationToken)
    {
        await _semaphoreSlim.WaitAsync(cancellationToken);
        return _releaser;
    }
    
    /// <inheritdoc />
    public async ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _semaphoreSlim.WaitAsync(timeout, cancellationToken);
        return _releaser;
    }

    /// <inheritdoc />
    public int CurrentCount => _semaphoreSlim.CurrentCount;

    /// <inheritdoc />
    public void Dispose()
    {
        _semaphoreSlim.Dispose();
    }
}