#pragma warning disable SEM0001

namespace Semaphores;

public sealed class AsyncSemaphore : IAsyncSemaphore
{
    private readonly SemaphoreSlim _semaphoreSlim;
    
    public AsyncSemaphore(int maxCount)
    {
        _semaphoreSlim = new(maxCount, maxCount);
    }

    /// <inheritdoc />
    public async ValueTask<AsyncSemaphoreReleaser> WaitAsync()
    {
        return await WaitAsync(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
    }
    
    /// <inheritdoc />
    public async ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout)
    {
        return await WaitAsync(timeout, CancellationToken.None).ConfigureAwait(false);
    }
    
    /// <inheritdoc />
    public async ValueTask<AsyncSemaphoreReleaser> WaitAsync(CancellationToken cancellationToken)
    {
        return await WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }
    
    /// <inheritdoc />
    public async ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        bool result = await _semaphoreSlim.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!result)
            throw new OperationCanceledException();
        return new AsyncSemaphoreReleaser(_semaphoreSlim);
    }

    /// <inheritdoc />
    public int CurrentCount => _semaphoreSlim.CurrentCount;

    /// <inheritdoc />
    public void Dispose()
    {
        _semaphoreSlim.Dispose();
    }
}