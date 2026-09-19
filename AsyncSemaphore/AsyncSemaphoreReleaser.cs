using System.Runtime.CompilerServices;

namespace Semaphores;

/// <summary>Releases an acquired permit at most once, including when the handle is copied or boxed.</summary>
public readonly struct AsyncSemaphoreReleaser : IDisposable
{
    // Copies must share the release decision. An exclusive gate at rest holds that decision itself, in
    // its epoch, so the handle points at the gate and allocates nothing. Every other acquisition gets
    // a ReleaseState of its own.
    private readonly object? _state;

    /// <summary>The epoch the permit was acquired under when <see cref="_state"/> is the gate. Unused next to a <see cref="ReleaseState"/>.</summary>
    private readonly long _epoch;

    /// <summary>Must run while the caller holds the permit and before it is published: that is what pins the epoch taken here.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal AsyncSemaphoreReleaser(AsyncSemaphore semaphore)
    {
        if (semaphore.TakesEpoch())
        {
            _state = semaphore;
            _epoch = semaphore.Epoch;
        }
        else
        {
            _state = new ReleaseState(semaphore);
            _epoch = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        var state = _state;

        if (state is AsyncSemaphore semaphore)
        {
            semaphore.ReleaseExclusive(_epoch);
        }
        else if (state is not null)
        {
            // The constructor stores nothing else here, so the second type check is skipped where the
            // framework provides the cast. netstandard2.0 would need a package reference of its own for it.
#if NETSTANDARD2_0
            ((ReleaseState)state).Dispose();
#else
            Unsafe.As<ReleaseState>(state).Dispose();
#endif
        }
    }

    // This state cannot be pooled: an arbitrarily old copy may still be disposed after a later
    // acquisition has started.
    private sealed class ReleaseState
    {
        private AsyncSemaphore? _semaphore;

        public ReleaseState(AsyncSemaphore semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
