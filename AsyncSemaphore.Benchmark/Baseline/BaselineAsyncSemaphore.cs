#pragma warning disable SEM0001

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace AsyncSemaphore.Benchmark.Baseline;

/// <summary>
/// Frozen snapshot of the core as of commit 6a827ec (per-instance overflow pool, before the shared one
/// of issue #589), kept so <see cref="AbBenchmarks"/> can A/B the working-tree core against a fixed
/// reference in the same run. Do not edit; regenerate from a newer commit if a new baseline is wanted.
/// </summary>
public sealed class BaselineAsyncSemaphore
{
    /// <summary>
    /// Smallest tick count whose (truncated) millisecond value is -1, i.e. the lowest timeout
    /// <see cref="SemaphoreSlim"/> accepts.
    /// </summary>
    private const long MinTimeoutTicks = -(2 * TimeSpan.TicksPerMillisecond) + 1;

    /// <summary>Largest tick count whose (truncated) millisecond value still fits in an <see cref="int"/>.</summary>
    private const long MaxTimeoutTicks = ((int.MaxValue + 1L) * TimeSpan.TicksPerMillisecond) - 1;

#if !NETSTANDARD2_0
    /// <summary>Spins a blocking wait makes on the fast path before it parks its thread.</summary>
    private const int SpinCountBeforeBlocking = 35 * 4;
#endif

    /// <summary>
    /// Positive values are available permits. Negative values are outstanding waiters
    /// (each of which has enqueued, or is committed to enqueueing, a node in <see cref="_waiters"/>).
    /// </summary>
    private int _count;

    // Both queues are created on first use. Neither is touched until a wait actually contends, and
    // an empty ConcurrentQueue costs ~840 B, so a gate that never contends pays for neither.
    private ConcurrentQueue<Waiter>? _waiters;
    private ConcurrentQueue<Waiter>? _pool;

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

    /// <summary>
    /// Set only on the instance behind an <see cref="Semaphores.UnpairedAsyncSemaphore"/>. Its acquisitions hand out
    /// no releaser, because its permits come back through <see cref="ReleaseUnpaired"/> instead.
    /// </summary>
    private readonly bool _unpaired;

    public BaselineAsyncSemaphore(int maxCount)
    {
        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "maxCount must be a positive integer.");
        }

        _count = maxCount;
    }

    /// <summary>
    /// Backs an <see cref="Semaphores.UnpairedAsyncSemaphore"/>, which validates the count. Zero is meaningful
    /// there because a permit can be published without a prior wait.
    /// </summary>
    internal BaselineAsyncSemaphore(int initialCount, bool unpaired)
    {
        _count = initialCount;
        _unpaired = unpaired;
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
            return TimedOut(timeout);
        }

        return EnqueueWaiter(timeout, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWait(out BaselineReleaser releaser)
    {
        ThrowIfDisposed();

        if (TryAcquireFast())
        {
            releaser = new BaselineReleaser(this);

            return true;
        }

        // A failed attempt never queues, so it leaves no waiter debt for a releaser to settle.
        releaser = default;

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BaselineReleaser Wait(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return new BaselineReleaser(this);
        }

        return WaitBlocking(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BaselineReleaser Wait(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return new BaselineReleaser(this);
        }

        if (timeout == TimeSpan.Zero)
        {
            ThrowTimedOut(timeout);
        }

        return WaitBlocking(timeout, cancellationToken);
    }

    /// <summary>
    /// <see cref="Semaphores.UnpairedAsyncSemaphore"/> counterpart of <see cref="WaitAsync(TimeSpan, CancellationToken)"/>:
    /// the same acquisition, but no releaser is created for it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ValueTask WaitUnpairedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return default;
        }

        if (timeout == TimeSpan.Zero)
        {
            return TimedOutUnpaired(timeout);
        }

        return EnqueueUnpairedWaiter(timeout, cancellationToken);
    }

    /// <summary><see cref="Semaphores.UnpairedAsyncSemaphore"/> counterpart of <see cref="Wait(TimeSpan, CancellationToken)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WaitUnpaired(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return;
        }

        if (timeout == TimeSpan.Zero)
        {
            ThrowTimedOut(timeout);
        }

        // Default in unpaired mode, so there is nothing to keep.
        _ = WaitBlocking(timeout, cancellationToken);
    }

    /// <summary><see cref="Semaphores.UnpairedAsyncSemaphore"/> counterpart of <see cref="TryWait"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryWaitUnpaired()
    {
        ThrowIfDisposed();

        return TryAcquireFast();
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

    /// <summary>
    /// Waits that have committed to the queue and are not settled yet. Test seam: a wait spins before
    /// it commits, so a test cannot tell from the outside whether it has queued.
    /// </summary>
    internal int QueuedWaiterCount
    {
        get
        {
            var count = Volatile.Read(ref _count);
            return count < 0 ? -count : 0;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>
    /// Returns a permit. Called exactly once per successful acquisition, by <see cref="BaselineReleaser.Dispose"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Release()
    {
        if (Interlocked.Increment(ref _count) <= 0)
        {
            ReleaseNextWaiter();
        }
    }

    /// <summary>
    /// Publishes a permit that no wait handed out. A paired release can never push the count past its
    /// initial value; an unpaired one can, so the wrap at <see cref="int.MaxValue"/> is guarded here
    /// (a wrapped count would read as waiter debt and spin in <see cref="ReleaseNextWaiter"/> forever).
    /// </summary>
    internal void ReleaseUnpaired()
    {
        var count = Volatile.Read(ref _count);

        while (true)
        {
            if (count == int.MaxValue)
            {
                ThrowSemaphoreFull();
            }

            var observed = Interlocked.CompareExchange(ref _count, count + 1, count);

            if (observed == count)
            {
                break;
            }

            count = observed;
        }

        if (count < 0)
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
        // The committed waiter creates the queue before its decrement, so this only misses on a stale read.
        var waiters = _waiters ?? CreateQueue(ref _waiters);

        while (true)
        {
            Waiter? waiter;
            var spinner = default(SpinWait);

            while (!waiters.TryDequeue(out waiter))
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
        var waiter = CommitWaiter(timeout, cancellationToken, synchronous: false, out var version);

        return waiter is null
            ? new ValueTask<BaselineReleaser>(new BaselineReleaser(this))
            : new ValueTask<BaselineReleaser>(waiter, version);
    }

    private ValueTask EnqueueUnpairedWaiter(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = CommitWaiter(timeout, cancellationToken, synchronous: false, out var version);

        return waiter is null ? default : new ValueTask(waiter, version);
    }

    /// <summary>
    /// Blocks the calling thread on a queued node. The node is woken inline by whichever thread
    /// completes it, so a blocked thread never depends on the thread pool to make progress.
    /// </summary>
    private BaselineReleaser WaitBlocking(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Integer milliseconds, already validated to fit. A finite budget covers the spin and the
        // commit as well as the park, so its clock starts here.
        var millisecondsTimeout = (int)(timeout.Ticks / TimeSpan.TicksPerMillisecond);
        var startTimestamp = millisecondsTimeout > 0 ? Stopwatch.GetTimestamp() : 0;

        // Spin on the fast path before committing to the queue. A parked thread costs a kernel wake-up
        // per handoff, and once one waiter is queued every release is handed over in queue order, so
        // threads that park behind a short critical section convoy. Spinning never jumps the queue:
        // the fast path only succeeds while no waiter is outstanding.
        var spinner = default(SpinWait);

        while (SpinBeforeBlocking(ref spinner))
        {
            if (TryAcquireFast())
            {
                return _unpaired ? default : new BaselineReleaser(this);
            }
        }

        var waiter = CommitWaiter(timeout, cancellationToken, synchronous: true, out var version);

        if (waiter is null)
        {
            return _unpaired ? default : new BaselineReleaser(this);
        }

        if (millisecondsTimeout > 0)
        {
            millisecondsTimeout = RemainingMilliseconds(millisecondsTimeout, Stopwatch.GetTimestamp() - startTimestamp);
        }

        return waiter.WaitSynchronously(timeout, millisecondsTimeout, cancellationToken, version);
    }

    /// <summary>
    /// What is left of a finite budget after <paramref name="elapsedTimestampTicks"/> <see cref="Stopwatch"/>
    /// ticks. The elapsed time rounds down, so a wait never times out early.
    /// </summary>
    internal static int RemainingMilliseconds(int millisecondsTimeout, long elapsedTimestampTicks)
    {
        var elapsedMilliseconds = elapsedTimestampTicks * 1000 / Stopwatch.Frequency;

        return elapsedMilliseconds >= millisecondsTimeout ? 0 : millisecondsTimeout - (int)elapsedMilliseconds;
    }

    /// <summary>Spins once while the budget lasts: the same budget <see cref="SemaphoreSlim"/> spends before it blocks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SpinBeforeBlocking(ref SpinWait spinner)
    {
#if NETSTANDARD2_0
        // No overload here can rule out Sleep(1), so stop as soon as SpinWait would start yielding.
        if (spinner.NextSpinWillYield)
        {
            return false;
        }

        spinner.SpinOnce();
#else
        if (spinner.Count >= SpinCountBeforeBlocking)
        {
            return false;
        }

        spinner.SpinOnce(sleep1Threshold: -1);
#endif

        return true;
    }

    /// <summary>
    /// Commits to waiting. Returns the enqueued node and the version its result must be read with, or
    /// null when the commit decrement found a permit after all. Inlined so that every caller keeps a
    /// straight-line copy with <paramref name="synchronous"/> folded to a constant.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Waiter? CommitWaiter(TimeSpan timeout, CancellationToken cancellationToken, bool synchronous, out short version)
    {
        // The node is rented (and the queue created, on first contention) up front so the
        // decrement-to-enqueue window a releaser spin-waits on stays as small as possible.
        var waiters = _waiters ?? CreateQueue(ref _waiters);
        var waiter = RentWaiter();

        // Read before the enqueue: afterwards the node can complete and be recycled at any time.
        version = waiter.Version;

        // The decrement is the commit point: a permit may have appeared since the fast path failed.
        if (Interlocked.Decrement(ref _count) >= 0)
        {
            // Never owned or armed, still clean.
            ReturnWaiter(waiter);

            return null;
        }

        waiter.SetOwner(this);

        // Arm before enqueueing: a claim can only happen after the enqueue, so the claimer always
        // observes fully-armed timer/registration fields when cleaning them up, and a blocking
        // waiter's wake-up is always registered ahead of its completion.
        // If cancellation fires first, the node is enqueued dead and settled by a later release.
        if (synchronous)
        {
            waiter.ArmSynchronous();
        }
        else if (timeout != Timeout.InfiniteTimeSpan || cancellationToken.CanBeCanceled)
        {
            waiter.ArmCancellation(timeout, cancellationToken);
        }

        waiters.Enqueue(waiter);

        return waiter;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Waiter RentWaiter()
    {
        var waiter = t_pooledWaiter;

        if (waiter is not null)
        {
            t_pooledWaiter = null;

            return waiter;
        }

        // Test before exchanging so an empty slot costs a read, not a locked write on a shared line.
        if (Volatile.Read(ref _pooledWaiter) is not null
            && (waiter = Interlocked.Exchange(ref _pooledWaiter, null)) is not null)
        {
            return waiter;
        }

        var pool = _pool;

        return pool is not null && pool.TryDequeue(out waiter) ? waiter : new Waiter();
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

        // Test before the CAS so a full slot costs a read, not a failed locked write.
        if (Volatile.Read(ref _pooledWaiter) is not null
            || Interlocked.CompareExchange(ref _pooledWaiter, waiter, null) is not null)
        {
            (_pool ?? CreateQueue(ref _pool)).Enqueue(waiter);
        }
    }

    /// <summary>
    /// Publishes a queue with a CAS so racing creators all end up on the same instance. The field is
    /// written exactly once, so plain reads elsewhere are safe: a stale null only lands back here.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ConcurrentQueue<Waiter> CreateQueue(ref ConcurrentQueue<Waiter>? location)
    {
        var created = new ConcurrentQueue<Waiter>();

        return Interlocked.CompareExchange(ref location, created, null) ?? created;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            ThrowObjectDisposed();
        }
    }

    /// <summary>
    /// Same contract as <see cref="SemaphoreSlim"/> (<c>(long)timeout.TotalMilliseconds</c> must lie in
    /// [-1, <see cref="int.MaxValue"/>]) as a single unsigned range compare on the raw ticks, which
    /// avoids the floating-point conversion on every timed wait.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (unchecked((ulong)(timeout.Ticks - MinTimeoutTicks)) > unchecked((ulong)(MaxTimeoutTicks - MinTimeoutTicks)))
        {
            ThrowTimeoutOutOfRange(timeout);
        }
    }

    // Throw and fault helpers are kept out of line so the inlined fast paths stay small.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowObjectDisposed()
    {
        throw new ObjectDisposedException(nameof(BaselineAsyncSemaphore));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTimeoutOutOfRange(TimeSpan timeout)
    {
        throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be -1 milliseconds (infinite) or a non-negative value <= Int32.MaxValue milliseconds.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSemaphoreFull()
    {
        throw new SemaphoreFullException();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTimedOut(TimeSpan timeout)
    {
        throw CreateTimeoutException(timeout);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<BaselineReleaser> TimedOut(TimeSpan timeout)
    {
        return new ValueTask<BaselineReleaser>(Task.FromException<BaselineReleaser>(CreateTimeoutException(timeout)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask TimedOutUnpaired(TimeSpan timeout)
    {
        return new ValueTask(Task.FromException(CreateTimeoutException(timeout)));
    }

    private static TimeoutException CreateTimeoutException(TimeSpan timeout)
    {
        return new TimeoutException($"The semaphore wait exceeded the timeout of {timeout}.");
    }

    private sealed class Waiter : IValueTaskSource<BaselineReleaser>, IValueTaskSource
    {
        private const int StatePending = 0;
        private const int StateClaimed = 1;
        private const int StateCancelled = 2;

        private static readonly TimerCallback TimeoutCallback = static state => OnTimeout((Waiter)state!);
        private static readonly Action<object?> CancellationCallback = static state => OnCancelled((Waiter)state!);
        private static readonly Action<object?> WakeCallback = static state => ((ManualResetEventSlim)state!).Set();

        private BaselineAsyncSemaphore _owner = null!;

        private ManualResetValueTaskSourceCore<BaselineReleaser> _core;
        private int _state;
        private bool _cancellable;
        private Timer? _timeoutTimer;
        private TimeSpan _timeout;
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationToken _cancellationToken;

        /// <summary>Created by the first blocking wait on this node, then reused for as long as the node is pooled.</summary>
        private ManualResetEventSlim? _wakeEvent;

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

                var timer = _timeoutTimer;

                if (timer is not null)
                {
#if NETSTANDARD2_0
                    timer.Dispose();
#else
                    // DisposeAsync completes synchronously only when no callback is in flight (it checks
                    // the callback count under the same lock Fire takes before running one), which proves
                    // no stale OnTimeout can ever touch this node again, so it is safe to pool.
                    if (timer.DisposeAsync().IsCompletedSuccessfully)
                    {
                        _timeoutTimer = null;
                    }
#endif
                }
            }

            var owner = _owner;

            _core.SetResult(owner._unpaired ? default : new BaselineReleaser(owner));
        }

        /// <summary>
        /// Prepares the node for a blocking waiter. Runs before the enqueue, while the core cannot yet
        /// complete, so the wake-up is always registered ahead of completion and the completing thread
        /// sets the event inline: no continuation is queued to the thread pool, which a blocked
        /// caller may be starving.
        /// </summary>
        public void ArmSynchronous()
        {
            // No timer or registration is armed: the blocked thread times itself out and cancels
            // itself in WaitSynchronously, racing the claim through the same state CAS. Even a wait
            // with no timeout or token takes that CAS, because Thread.Interrupt can abandon it.
            _cancellable = true;
            _core.RunContinuationsAsynchronously = false;
            _core.OnCompleted(WakeCallback, _wakeEvent ??= new ManualResetEventSlim(), _core.Version, ValueTaskSourceOnCompletedFlags.None);
        }

        /// <param name="timeout">The requested budget, reported when the wait times out.</param>
        /// <param name="millisecondsTimeout">What is left of <paramref name="timeout"/> to park for, or -1 for no limit.</param>
        public BaselineReleaser WaitSynchronously(TimeSpan timeout, int millisecondsTimeout, CancellationToken cancellationToken, short token)
        {
            var wakeEvent = _wakeEvent!;
            var interrupted = false;
            bool woken;

            try
            {
                woken = wakeEvent.Wait(millisecondsTimeout, cancellationToken);
            }
            catch (Exception exception)
            {
                // Cancellation, or Thread.Interrupt. The wait may only be abandoned while the node can
                // still be cancelled; it then stays queued as a dead entry for a later release to settle.
                if (Interlocked.CompareExchange(ref _state, StateCancelled, StatePending) == StatePending)
                {
                    throw;
                }

                interrupted = exception is ThreadInterruptedException;
                woken = false;
            }

            if (!woken)
            {
                if (Interlocked.CompareExchange(ref _state, StateCancelled, StatePending) == StatePending)
                {
                    throw CreateTimeoutException(timeout);
                }

                // A releaser claimed the node first, so the permit is ours and completion is imminent.
                // It has to be collected even through an interrupt, or it would be lost with the node.
                while (true)
                {
                    try
                    {
                        wakeEvent.Wait();

                        break;
                    }
                    catch (ThreadInterruptedException)
                    {
                        interrupted = true;
                    }
                }
            }

            // The completing thread has already read the flag and set the event, so the node can go
            // back to its pooled (asynchronous) shape before GetResult recycles it.
            wakeEvent.Reset();
            _core.RunContinuationsAsynchronously = true;

            var result = GetResult(token);

            if (interrupted)
            {
                // The acquisition won the race, so the interrupt is left pending for the thread's
                // next blocking call instead of being swallowed. Re-raised only once the result is in
                // hand: recycling the node can contend on the pool's lock, and a pending interrupt
                // surfacing there would take the permit down with it.
                Thread.CurrentThread.Interrupt();
            }

            return result;
        }

        public void ArmCancellation(TimeSpan timeout, CancellationToken cancellationToken)
        {
            _cancellable = true;
            _timeout = timeout;
            _cancellationToken = cancellationToken;

            if (cancellationToken.CanBeCanceled)
            {
#if NETSTANDARD2_0
                _cancellationRegistration = cancellationToken.Register(CancellationCallback, this);
#else
                // The callback only performs a CAS and completes the core, so it needs no ExecutionContext.
                _cancellationRegistration = cancellationToken.UnsafeRegister(CancellationCallback, this);
#endif
            }

            if (timeout != Timeout.InfiniteTimeSpan && Volatile.Read(ref _state) == StatePending)
            {
                // Integer milliseconds (already validated to fit) so the Timer constructor skips its own
                // floating-point TimeSpan conversion.
                var timer = new Timer(TimeoutCallback, this, (int)(timeout.Ticks / TimeSpan.TicksPerMillisecond), Timeout.Infinite);

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
                // A timer callback was in flight when the claimer disposed the timer (Timer.Dispose
                // does not wait for it, unlike CancellationTokenRegistration.Dispose), so a stale
                // OnTimeout may still hold this node. Dropping it instead of pooling leaves _state
                // at StateClaimed, so the stale CAS fails without touching a recycled core.
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

        /// <summary>The unpaired wait's view of the same node: same settlement and pooling, no releaser to return.</summary>
        void IValueTaskSource.GetResult(short token)
        {
            GetResult(token);
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
