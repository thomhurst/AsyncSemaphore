namespace Semaphores;

public interface IAsyncSemaphore : IDisposable
{
    /// <summary>Acquires a permit, waiting asynchronously if none is available.</summary>
    /// <returns>A handle that releases the permit when disposed. Consume the returned ValueTask only once.</returns>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    ValueTask<AsyncSemaphoreReleaser> WaitAsync();

    /// <summary>Acquires a permit within the specified timeout.</summary>
    /// <param name="timeout">Truncated to whole milliseconds: -1 waits indefinitely, 0 attempts immediate acquisition,
    /// and positive values wait up to that duration. The truncated value must not exceed Int32.MaxValue.</param>
    /// <returns>A handle that releases the permit when disposed. Consume the returned ValueTask only once.</returns>
    /// <exception cref="TimeoutException">No permit was acquired before the timeout elapsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The truncated timeout is outside [-1, Int32.MaxValue] milliseconds.</exception>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout);

    /// <summary>Acquires a permit, allowing cancellation while waiting.</summary>
    /// <param name="cancellationToken">Cancels acquisition. Cancellation after acquisition does not release the permit.</param>
    /// <returns>A handle that releases the permit when disposed. Consume the returned ValueTask only once.</returns>
    /// <exception cref="OperationCanceledException">Cancellation won before acquisition. An already cancelled token throws synchronously.</exception>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    ValueTask<AsyncSemaphoreReleaser> WaitAsync(CancellationToken cancellationToken);

    /// <summary>Acquires a permit within the specified timeout, allowing cancellation while waiting.</summary>
    /// <param name="timeout">Truncated to whole milliseconds in [-1, Int32.MaxValue]; -1 waits indefinitely and 0 attempts immediate acquisition.</param>
    /// <param name="cancellationToken">Cancels acquisition. Cancellation after acquisition does not release the permit.</param>
    /// <returns>A handle that releases the permit when disposed. Consume the returned ValueTask only once.</returns>
    /// <exception cref="TimeoutException">No permit was acquired before the timeout elapsed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation won before acquisition. An already cancelled token throws synchronously.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The truncated timeout is outside [-1, Int32.MaxValue] milliseconds.</exception>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Gets a snapshot of the available permit count, never less than zero.</summary>
    int CurrentCount { get; }
}
