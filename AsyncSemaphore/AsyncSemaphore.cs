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
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync()
    {
        var task = _semaphoreSlim.WaitAsync();

        if (task.Status == TaskStatus.RanToCompletion)
        {
            return new ValueTask<AsyncSemaphoreReleaser>(new AsyncSemaphoreReleaser(_semaphoreSlim));
        }

        return AwaitAndReturn(task);
    }

    /// <inheritdoc />
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout)
    {
        var task = _semaphoreSlim.WaitAsync(timeout);

        if (task.Status == TaskStatus.RanToCompletion)
        {
            return task.Result
                ? new ValueTask<AsyncSemaphoreReleaser>(new AsyncSemaphoreReleaser(_semaphoreSlim))
                : throw new TimeoutException($"The semaphore wait exceeded the timeout of {timeout}.");
        }

        return AwaitAndReturn(task, timeout);
    }

    /// <inheritdoc />
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync(CancellationToken cancellationToken)
    {
        var task = _semaphoreSlim.WaitAsync(cancellationToken);

        if (task.Status == TaskStatus.RanToCompletion)
        {
            return new ValueTask<AsyncSemaphoreReleaser>(new AsyncSemaphoreReleaser(_semaphoreSlim));
        }

        return AwaitAndReturn(task);
    }

    /// <inheritdoc />
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var task = _semaphoreSlim.WaitAsync(timeout, cancellationToken);

        if (task.Status == TaskStatus.RanToCompletion)
        {
            return task.Result
                ? new ValueTask<AsyncSemaphoreReleaser>(new AsyncSemaphoreReleaser(_semaphoreSlim))
                : throw new TimeoutException($"The semaphore wait exceeded the timeout of {timeout}.");
        }

        return AwaitAndReturn(task, timeout);
    }

    private async ValueTask<AsyncSemaphoreReleaser> AwaitAndReturn(Task task)
    {
        await task.ConfigureAwait(false);
        return new AsyncSemaphoreReleaser(_semaphoreSlim);
    }

    private async ValueTask<AsyncSemaphoreReleaser> AwaitAndReturn(Task<bool> task, TimeSpan timeout)
    {
        var acquired = await task.ConfigureAwait(false);

        if (!acquired)
        {
            throw new TimeoutException($"The semaphore wait exceeded the timeout of {timeout}.");
        }

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