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

    /// <summary>
    /// Takes a permit only if one is available right now. Never blocks and never queues.
    /// </summary>
    /// <param name="releaser">The handle that releases the permit when this returns true; otherwise a default handle, which releases nothing.</param>
    /// <returns>true when a permit was taken.</returns>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    bool TryWait(out AsyncSemaphoreReleaser releaser);

    /// <summary>
    /// Blocks the calling thread until a permit is acquired. Prefer <see cref="WaitAsync()"/> unless
    /// blocking the caller is intended.
    /// </summary>
    /// <returns>The handle that releases the permit.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    AsyncSemaphoreReleaser Wait(CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks the calling thread until a permit is acquired or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <returns>The handle that releases the permit.</returns>
    /// <exception cref="TimeoutException">No permit was acquired within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is neither -1 milliseconds nor in [0, <see cref="int.MaxValue"/>] milliseconds.</exception>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    AsyncSemaphoreReleaser Wait(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="SemaphoreSlim.CurrentCount"/>
    int CurrentCount { get; }
}
