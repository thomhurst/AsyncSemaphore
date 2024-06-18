namespace Semaphores;

public interface IAsyncSemaphore : IDisposable
{
    /// <inheritdoc cref="SemaphoreSlim.WaitAsync()"/>
    ValueTask<IDisposable> WaitAsync();

    /// <inheritdoc cref="SemaphoreSlim.WaitAsync(TimeSpan)"/>
    ValueTask<IDisposable> WaitAsync(TimeSpan timeout);

    /// <inheritdoc cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
    ValueTask<IDisposable> WaitAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/>
    ValueTask<IDisposable> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <inheritdoc cref="SemaphoreSlim.CurrentCount"/>
    int CurrentCount { get; }
}