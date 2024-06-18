namespace Semaphores;

public interface IAsyncSemaphore : IDisposable
{
    /// <inheritdoc cref="SemaphoreSlim.WaitAsync()"/>
    ValueTask<AsyncSemaphoreReleaser> WaitAsync();

    /// <inheritdoc cref="SemaphoreSlim.WaitAsync(TimeSpan)"/>
    ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout);

    /// <inheritdoc cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
    ValueTask<AsyncSemaphoreReleaser> WaitAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/>
    ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <inheritdoc cref="SemaphoreSlim.CurrentCount"/>
    int CurrentCount { get; }
}