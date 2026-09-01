#pragma warning disable SEM0001

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace AsyncSemaphore.Benchmark.Baseline;

/// <summary>
/// Frozen snapshot of the core as of commit 66ad5f7 (lock-free rewrite, before the micro-optimisation
/// pass), kept so <see cref="AbBenchmarks"/> can A/B the working-tree core against a fixed reference
/// in the same run. Do not edit; regenerate from a newer commit if a new baseline is wanted.
/// </summary>
public sealed class BaselineAsyncSemaphore
{
    /// <summary>
    /// Positive values are available permits. Negative values are outstanding waiters
    /// (each of which has enqueued, or is committed to enqueueing, a node in <see cref="_waiters"/>).
    /// </summary>
    private int _count;

    private readonly ConcurrentQueue<Waiter> _waiters = new();
    private readonly ConcurrentQueue<Waiter> _pool = new();

    /// <summary>Single-slot fast cache in front of <see cref="_pool"/> for the common ping-pong case.</summary>
    private Waiter? _pooledWaiter;

    /// <summary>
    /// Thread-local node cache tried before the shared pool: rent and return on the same thread
    /// cost no interlocked operations at all. May hold a node last used by another semaphore;
    /// nodes are re-owned on rent.
    /// </summary>
    [ThreadStatic]
    private static Waiter? t_pooledWaiter;

    private bool _disposed;

    public BaselineAsyncSemaphore(int maxCount)
    {
        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "maxCount must be a positive integer.");
        }

        _count = maxCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<BaselineReleaser> WaitAsync()
    {
        ThrowIfDisposed();

        if (TryAcquireFast())
        {
            return new ValueTask<BaselineReleaser>(new BaselineReleaser(this));
        }

        return EnqueueWaiter(Timeout.InfiniteTimeSpan, default);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<BaselineReleaser> WaitAsync(TimeSpan timeout)
    {
        return WaitAsync(timeout, CancellationToken.None);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<BaselineReleaser> WaitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return new ValueTask<BaselineReleaser>(new BaselineReleaser(this));
        }

        return EnqueueWaiter(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<BaselineReleaser> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return new ValueTask<BaselineReleaser>(new BaselineReleaser(this));
        }

        if (timeout == TimeSpan.Zero)
        {
            // A zero timeout is a single attempt: fail here without renting a node, arming a timer,
            // or creating waiter debt that a concurrent releaser would have to spin on and settle.
            return new ValueTask<BaselineReleaser>(Task.FromException<BaselineReleaser>(CreateTimeoutException(timeout)));
        }

        return EnqueueWaiter(timeout, cancellationToken);
    }

    /// <summary>
    /// Optimistically takes a permit while the count is positive, without ever driving it negative.
    /// Only the slow path's decrement creates waiter debt, which lets it rent its node up front and
    /// keep the decrement-to-enqueue window (which a releaser spin-waits on) as small as possible.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireFast()
    {
        var count = Volatile.Read(ref _count);

        while (count > 0)
        {
            var observed = Interlocked.CompareExchange(ref _count, count - 1, count);

            if (observed == count)
            {
                return true;
            }

            count = observed;
        }

        return false;
    }

    public int CurrentCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var count = Volatile.Read(ref _count);
            return count > 0 ? count : 0;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>
    /// Returns a permit. Called exactly once per successful acquisition, by <see cref="BaselineReleaser.Dispose"/>.
    /// </summary>
    internal void Release()
    {
        if (Interlocked.Increment(ref _count) <= 0)
        {
            ReleaseNextWaiter();
        }
    }

    /// <summary>
    /// The increment observed outstanding waiters, so this permit must be handed to exactly one of them.
    /// The dequeue is the settlement token: whoever dequeues a node owns settling it, so a node
    /// cancelled while queued is compensated here (its debt removed from the counter) and the permit
    /// is re-deposited — going around again if the re-deposit still observes outstanding waiters.
    /// </summary>
    private void ReleaseNextWaiter()
    {
        while (true)
        {
            Waiter? waiter;
            var spinner = default(SpinWait);

            while (!_waiters.TryDequeue(out waiter))
            {
                // A decrement that goes negative is committed to enqueueing, so a node will appear.
                spinner.SpinOnce();
            }

            if (waiter.TryClaim())
            {
                waiter.SetAcquired();
                return;
            }

            // Dead (cancelled/timed-out) node: remove its debt and re-deposit the permit.
            if (Interlocked.Increment(ref _count) > 0)
            {
                return;
            }
        }
    }

    private ValueTask<BaselineReleaser> EnqueueWaiter(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = t_pooledWaiter;

        if (waiter is not null)
        {
            t_pooledWaiter = null;
        }
        else if ((waiter = Interlocked.Exchange(ref _pooledWaiter, null)) is null && !_pool.TryDequeue(out waiter))
        {
            waiter = new Waiter();
        }

        waiter.SetOwner(this);

        var version = waiter.Version;

        // The decrement is the commit point: a permit may have appeared since the fast path failed.
        // The node is rented up front so the decrement-to-enqueue window a releaser spin-waits on
        // stays as small as possible.
        if (Interlocked.Decrement(ref _count) >= 0)
        {
            // Never armed, still clean.
            ReturnWaiter(waiter);

            return new ValueTask<BaselineReleaser>(new BaselineReleaser(this));
        }

        // Arm cancellation before enqueueing: a claim can only happen after the enqueue, so the
        // claimer always observes fully-armed timer/registration fields when cleaning them up.
        // If cancellation fires first, the node is enqueued dead and settled by a later release.
        if (timeout != Timeout.InfiniteTimeSpan || cancellationToken.CanBeCanceled)
        {
            waiter.ArmCancellation(timeout, cancellationToken);
        }

        _waiters.Enqueue(waiter);

        return new ValueTask<BaselineReleaser>(waiter, version);
    }

    private void ReturnWaiter(Waiter waiter)
    {
        if (t_pooledWaiter is null)
        {
            // Un-own the node so a cached node does not root this semaphore from thread-local storage.
            waiter.ClearOwner();
            t_pooledWaiter = waiter;

            return;
        }

        if (Interlocked.CompareExchange(ref _pooledWaiter, waiter, null) != null)
        {
            _pool.Enqueue(waiter);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BaselineAsyncSemaphore));
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        var totalMilliseconds = (long)timeout.TotalMilliseconds;

        if (totalMilliseconds < Timeout.Infinite || totalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be -1 milliseconds (infinite) or a non-negative value <= Int32.MaxValue milliseconds.");
        }
    }

    private static TimeoutException CreateTimeoutException(TimeSpan timeout)
    {
        return new TimeoutException($"The semaphore wait exceeded the timeout of {timeout}.");
    }

    private sealed class Waiter : IValueTaskSource<BaselineReleaser>
    {
        private const int StatePending = 0;
        private const int StateClaimed = 1;
        private const int StateCancelled = 2;

        private static readonly TimerCallback TimeoutCallback = static state => OnTimeout((Waiter)state!);
        private static readonly Action<object?> CancellationCallback = static state => OnCancelled((Waiter)state!);

        private BaselineAsyncSemaphore _owner = null!;

        private ManualResetValueTaskSourceCore<BaselineReleaser> _core;
        private int _state;
        private bool _cancellable;
        private Timer? _timeoutTimer;
        private TimeSpan _timeout;
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationToken _cancellationToken;

        public Waiter()
        {
            _core.RunContinuationsAsynchronously = true;
        }

        public short Version
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _core.Version;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOwner(BaselineAsyncSemaphore owner)
        {
            _owner = owner;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearOwner()
        {
            _owner = null!;
        }

        public bool TryClaim()
        {
            // Only cancellation can race a claim, and the dequeue already guarantees a single
            // claimer, so a waiter that can never be cancelled needs no interlocked claim.
            return !_cancellable
                || Interlocked.CompareExchange(ref _state, StateClaimed, StatePending) == StatePending;
        }

        public void SetAcquired()
        {
            if (_cancellable)
            {
                // The cancellation callbacks lose the CAS and return immediately, so these cannot deadlock.
                _cancellationRegistration.Dispose();
                _timeoutTimer?.Dispose();
            }

            _core.SetResult(new BaselineReleaser(_owner));
        }

        public void ArmCancellation(TimeSpan timeout, CancellationToken cancellationToken)
        {
            _cancellable = true;
            _timeout = timeout;
            _cancellationToken = cancellationToken;

            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.Register(CancellationCallback, this);
            }

            if (timeout != Timeout.InfiniteTimeSpan && Volatile.Read(ref _state) == StatePending)
            {
                var timer = new Timer(TimeoutCallback, this, timeout, Timeout.InfiniteTimeSpan);

                _timeoutTimer = timer;

                // The cancellation callback may have fired before it could observe the timer.
                if (Volatile.Read(ref _state) != StatePending)
                {
                    timer.Dispose();
                }
            }
        }

        private static void OnTimeout(Waiter waiter)
        {
            if (Interlocked.CompareExchange(ref waiter._state, StateCancelled, StatePending) != StatePending)
            {
                return;
            }

            waiter._cancellationRegistration.Dispose();
            waiter._timeoutTimer?.Dispose();

            waiter._core.SetException(CreateTimeoutException(waiter._timeout));
        }

        private static void OnCancelled(Waiter waiter)
        {
            if (Interlocked.CompareExchange(ref waiter._state, StateCancelled, StatePending) != StatePending)
            {
                return;
            }

            waiter._timeoutTimer?.Dispose();

            waiter._core.SetException(new OperationCanceledException(waiter._cancellationToken));
        }

        public BaselineReleaser GetResult(short token)
        {
            // Throws for cancelled/timed-out waiters, which must not be pooled:
            // their node is still queued until a release dequeues and settles it.
            var result = _core.GetResult(token);

            if (_timeoutTimer is not null)
            {
                // Timer.Dispose does not wait for an in-flight callback (unlike
                // CancellationTokenRegistration.Dispose), so a stale OnTimeout may still
                // hold this node. Dropping it instead of pooling leaves _state at
                // StateClaimed, so the stale CAS fails without touching a recycled core.
                return result;
            }

            _core.Reset();

            if (_cancellable)
            {
                _cancellable = false;
                _state = StatePending;
                _cancellationRegistration = default;
                _cancellationToken = default;
            }

            _owner.ReturnWaiter(this);

            return result;
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            return _core.GetStatus(token);
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _core.OnCompleted(continuation, state, token, flags);
        }
    }
}
