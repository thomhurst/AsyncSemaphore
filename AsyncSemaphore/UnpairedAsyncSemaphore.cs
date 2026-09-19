using System.Runtime.CompilerServices;

namespace Semaphores;

/// <summary>
/// A counting semaphore whose waits and releases are not paired: a wait hands out no releaser, and
/// <see cref="Release"/> can be called by code that never waited. Use it as a wake-up signal or as a
/// permit broker whose ownership is tracked elsewhere.
/// </summary>
/// <remarks>
/// Nothing stops a permit from being leaked or released twice here. Prefer <see cref="AsyncSemaphore"/>,
/// whose releaser (and analyzers) guarantee one release per acquisition, unless the release genuinely
/// cannot come from the acquirer. It runs on the same core as <see cref="AsyncSemaphore"/>.
/// </remarks>
public sealed class UnpairedAsyncSemaphore : IDisposable
{
    private readonly AsyncSemaphore _semaphore;

    /// <param name="initialCount">The permits available up front. May be zero; the count has no upper bound other than <see cref="int.MaxValue"/>.</param>
    public UnpairedAsyncSemaphore(int initialCount)
    {
        if (initialCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCount), initialCount, "initialCount must not be negative.");
        }

        _semaphore = new AsyncSemaphore(initialCount, unpaired: true);
    }

    /// <inheritdoc cref="SemaphoreSlim.CurrentCount"/>
    public int CurrentCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _semaphore.CurrentCount;
    }

    /// <inheritdoc cref="AsyncSemaphore.QueuedWaiterCount"/>
    internal int QueuedWaiterCount => _semaphore.QueuedWaiterCount;

    /// <summary>Waits for a permit. The caller owns returning it, if it is ever to be returned, through <see cref="Release"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        return _semaphore.WaitUnpairedAsync(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <inheritdoc cref="WaitAsync(CancellationToken)"/>
    /// <exception cref="TimeoutException">No permit was acquired within <paramref name="timeout"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is neither -1 milliseconds nor in [0, <see cref="int.MaxValue"/>] milliseconds.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return _semaphore.WaitUnpairedAsync(timeout, cancellationToken);
    }

    /// <summary>Blocks the calling thread until a permit is acquired.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Wait(CancellationToken cancellationToken = default)
    {
        _semaphore.WaitUnpaired(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <inheritdoc cref="Wait(CancellationToken)"/>
    /// <exception cref="TimeoutException">No permit was acquired within <paramref name="timeout"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is neither -1 milliseconds nor in [0, <see cref="int.MaxValue"/>] milliseconds.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Wait(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _semaphore.WaitUnpaired(timeout, cancellationToken);
    }

    /// <summary>Takes a permit only if one is available right now. Never blocks and never queues.</summary>
    /// <exception cref="ObjectDisposedException">The semaphore has been disposed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWait()
    {
        return _semaphore.TryWaitUnpaired();
    }

    /// <summary>Publishes one permit, waking the longest-queued waiter if there is one. No prior wait is required.</summary>
    /// <exception cref="SemaphoreFullException">The count is already <see cref="int.MaxValue"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release()
    {
        _semaphore.ReleaseUnpaired();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
