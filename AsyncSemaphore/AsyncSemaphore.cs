#pragma warning disable SEM0001

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace Semaphores;

public sealed class AsyncSemaphore : IAsyncSemaphore
{
    /// <summary>
    /// Smallest tick count whose (truncated) millisecond value is -1, i.e. the lowest timeout
    /// <see cref="SemaphoreSlim"/> accepts.
    /// </summary>
    private const long MinTimeoutTicks = -(2 * TimeSpan.TicksPerMillisecond) + 1;

    /// <summary>Largest tick count whose (truncated) millisecond value still fits in an <see cref="int"/>.</summary>
    private const long MaxTimeoutTicks = ((int.MaxValue + 1L) * TimeSpan.TicksPerMillisecond) - 1;

    /// <summary>
    /// Acquisitions that keep a release state of their own after a blocking wait last started to spin,
    /// before an exclusive gate goes back to releasing through its epoch.
    /// </summary>
    private const byte SpinnerCredit = 64;

    /// <summary><see cref="_spinnerCredit"/> of a gate that never releases through an epoch. Never counted down.</summary>
    private const byte AlwaysAllocate = byte.MaxValue;

    /// <summary>
    /// Most nodes the process-wide overflow pool keeps. Caps what a burst of waiters can leave behind
    /// for the rest of the process; a node returned past it is dropped.
    /// </summary>
    internal const int OverflowPoolCapacity = 256;

    /// <summary>
    /// Times a single wait can be overtaken (see <see cref="TryOvertake"/>) before it is handed the
    /// permit directly. It bounds how far a queued waiter can fall behind callers that arrived after
    /// it. Measured on the workload of issue #589: most of the gain is there by 16, none past 64.
    /// </summary>
    internal const int MaxOvertakes = 16;

#if !NETSTANDARD2_0
    /// <summary>Spins a blocking wait makes on the fast path before it parks its thread.</summary>
    private const int SpinCountBeforeBlocking = 35 * 4;
#endif

    /// <summary>
    /// Positive values are available permits. Negative values are outstanding waiters
    /// (each of which has enqueued, or is committed to enqueueing, a node in <see cref="_waiters"/>).
    /// </summary>
    private int _count;

    // Created on first use. It is not touched until a wait actually contends, and an empty
    // ConcurrentQueue costs ~840 B, so a gate that never contends does not pay for it.
    private WaiterQueue? _waiters;

    /// <summary>Single-slot fast cache in front of <see cref="OverflowPool"/> for the common ping-pong case.</summary>
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
    /// Set only on the instance behind an <see cref="UnpairedAsyncSemaphore"/>. Its acquisitions hand out
    /// no releaser, because its permits come back through <see cref="ReleaseUnpaired"/> instead.
    /// </summary>
    private readonly bool _unpaired;

    /// <summary>
    /// Generation of an exclusive gate's permit. Exclusive means a single permit with a handle handed
    /// out for it, so that at most one acquisition is outstanding at a time, which is what lets this
    /// stand in for the release state that is otherwise allocated per acquisition. A handle records
    /// the value it was acquired under, and a release has to advance exactly that value: among all
    /// copies of a handle one release wins, and a copy that outlives its acquisition never matches a
    /// later one. 64 bits, so it never wraps back onto a value a stale copy still holds.
    /// </summary>
    private long _epoch;

    /// <summary>
    /// Zero while an exclusive gate releases through <see cref="_epoch"/>. Anything else means the
    /// acquisition gets a release state of its own: <see cref="AlwaysAllocate"/> on a gate that is not
    /// exclusive, and otherwise what is left of <see cref="SpinnerCredit"/>.
    /// <para>
    /// The epoch sits next to <see cref="_count"/>, which a blocking wait reads in a loop while it spins.
    /// A release through the epoch writes that cache line twice, and spinning readers pull it away in
    /// between: four blocking threads on one gate measured 15% slower. An allocated state is private to
    /// the holder's core, so a gate with spinners keeps the release it always had. Queued async waiters
    /// do not read the line, and for them the epoch measured faster than the allocation. Both kinds of
    /// handle stay valid side by side, because a handle names the state it releases through and only
    /// one acquisition is live at a time.
    /// </para>
    /// Written without synchronization: a lost update only moves the switch by a few acquisitions.
    /// </summary>
    private byte _spinnerCredit;

    public AsyncSemaphore(int maxCount)
    {
        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "maxCount must be a positive integer.");
        }

        _count = maxCount;
        _spinnerCredit = maxCount == 1 ? (byte)0 : AlwaysAllocate;
    }

    /// <summary>
    /// Backs an <see cref="UnpairedAsyncSemaphore"/>, which validates the count. Zero is meaningful
    /// there because a permit can be published without a prior wait.
    /// </summary>
    internal AsyncSemaphore(int initialCount, bool unpaired)
    {
        _count = initialCount;
        _unpaired = unpaired;
        _spinnerCredit = AlwaysAllocate;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync()
    {
        ThrowIfDisposed();

        if (TryAcquireFast())
        {
            return new ValueTask<AsyncSemaphoreReleaser>(new AsyncSemaphoreReleaser(this));
        }

        return EnqueueWaiter(Timeout.InfiniteTimeSpan, default);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout)
    {
        return WaitAsync(timeout, CancellationToken.None);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return new ValueTask<AsyncSemaphoreReleaser>(new AsyncSemaphoreReleaser(this));
        }

        return EnqueueWaiter(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return new ValueTask<AsyncSemaphoreReleaser>(new AsyncSemaphoreReleaser(this));
        }

        return EnqueueTimedWaiter(timeout, cancellationToken);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWait(out AsyncSemaphoreReleaser releaser)
    {
        ThrowIfDisposed();

        if (TryAcquireFast())
        {
            releaser = new AsyncSemaphoreReleaser(this);

            return true;
        }

        // A failed attempt never queues, so it leaves no waiter debt for a releaser to settle.
        releaser = default;

        return false;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AsyncSemaphoreReleaser Wait(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return new AsyncSemaphoreReleaser(this);
        }

        return WaitBlocking(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AsyncSemaphoreReleaser Wait(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquireFast())
        {
            return new AsyncSemaphoreReleaser(this);
        }

        if (timeout == TimeSpan.Zero)
        {
            ThrowTimedOut(timeout);
        }

        return WaitBlocking(timeout, cancellationToken);
    }

    /// <summary>
    /// <see cref="UnpairedAsyncSemaphore"/> counterpart of <see cref="WaitAsync(TimeSpan, CancellationToken)"/>:
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

    /// <summary><see cref="UnpairedAsyncSemaphore"/> counterpart of <see cref="Wait(TimeSpan, CancellationToken)"/>.</summary>
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

    /// <summary><see cref="UnpairedAsyncSemaphore"/> counterpart of <see cref="TryWait"/>.</summary>
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

    /// <inheritdoc />
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

    /// <summary>
    /// Whether a grant is published and has been neither collected nor overtaken yet. Test seam: from
    /// the outside, a grant that was handed over directly looks like one whose hop has already run.
    /// </summary>
    internal bool HasPublishedGrant => _waiters?.InFlight is not null;

    /// <summary>Nodes parked in the process-wide overflow pool. Test seam for its retention cap.</summary>
    internal static int OverflowPoolCount => OverflowPool.Count;

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>
    /// Returns a permit. Called exactly once per successful acquisition, by <see cref="AsyncSemaphoreReleaser.Dispose"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Release()
    {
        if (Interlocked.Increment(ref _count) <= 0)
        {
            ReleaseNextWaiter();
        }
    }

    /// <summary>Decides how the acquisition being handed out will release: through <see cref="Epoch"/>, or through a state of its own.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TakesEpoch()
    {
        var credit = _spinnerCredit;

        if (credit == 0)
        {
            return true;
        }

        if (credit != AlwaysAllocate)
        {
            _spinnerCredit = (byte)(credit - 1);
        }

        return false;
    }

    /// <summary>
    /// The epoch the permit is held under. Only meaningful to the thread that holds the permit and has
    /// not published a handle for it yet: nothing can advance the epoch until that handle is disposed.
    /// </summary>
    internal long Epoch
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _epoch);
    }

    /// <summary>Whether the next acquisition would release through the epoch. Test seam: nothing else shows which kind of handle was handed out.</summary>
    internal bool ReleasesThroughEpoch => _spinnerCredit == 0;

    /// <summary>
    /// <see cref="Release"/> for a handle that took the epoch, called by every copy of it. The exchange is
    /// the at-most-once decision. It comes before the permit goes back, so whoever takes the permit
    /// next reads the advanced epoch, and no handle older than that acquisition can match it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReleaseExclusive(long epoch)
    {
        if (Interlocked.CompareExchange(ref _epoch, epoch + 1, epoch) == epoch)
        {
            Release();
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

        // A waiter whose grant was overtaken left the queue before anything that is in it now, so it
        // goes first. It was claimed when it was dequeued.
        var overtaken = waiters.Overtaken;

        if (overtaken is not null)
        {
            waiters.Overtaken = null;
            Grant(waiters, overtaken);

            return;
        }

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
                Grant(waiters, waiter);
                return;
            }

            // Dead (cancelled/timed-out) node: remove its debt and re-deposit the permit.
            if (Interlocked.Increment(ref _count) > 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Exclusive means a single permit with a handle handed out for it, so at most one acquisition is
    /// outstanding and only its holder ever releases.
    /// </summary>
    private bool IsExclusive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _spinnerCredit != AlwaysAllocate;
    }

    /// <summary>
    /// Hands the permit to a claimed waiter. On an exclusive gate the grant is published while the hop
    /// that resumes the waiter is on its way, so that <see cref="TryOvertake"/> can use the gate meanwhile.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Grant(WaiterQueue waiters, Waiter waiter)
    {
        if (waiter.CanBeOvertaken && IsExclusive)
        {
            PublishGrant(waiters, waiter);

            return;
        }

        waiter.SetAcquired();
    }

    /// <summary>Kept out of line so the direct handoff stays as small as it was.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PublishGrant(WaiterQueue waiters, Waiter waiter)
    {
        // Published before the hop is queued, so the hop always finds its own grant or none.
        Volatile.Write(ref waiters.InFlight, waiter);
        waiter.QueueGrant();
    }

    /// <summary>
    /// Takes the permit from a waiter it was granted to but whose continuation has not started yet.
    /// <para>
    /// A granted waiter resumes on the thread pool, and until it does the gate is owned and idle. Behind
    /// a short critical section that idle time is most of the cost: every caller that arrives in it
    /// queues, each of them pays the same hop in turn, and the waits convoy. A caller that is already
    /// running uses the gate in that gap instead. The waiter it overtook is served by its release,
    /// ahead of the queue, so queued waiters still leave in order, and after
    /// <see cref="MaxOvertakes"/> times it is handed the permit directly.
    /// </para>
    /// Only ever succeeds on an exclusive gate, because nothing else publishes a grant. That is what
    /// makes the bookkeeping sound: the caller now holds the only permit, so nobody can release until
    /// it does, and <see cref="WaiterQueue.Overtaken"/> has a single writer and a single reader.
    /// </summary>
    private bool TryOvertake()
    {
        var waiters = _waiters;

        if (waiters is null)
        {
            return false;
        }

        var waiter = Volatile.Read(ref waiters.InFlight);

        if (waiter is null || Interlocked.CompareExchange(ref waiters.InFlight, null, waiter) != waiter)
        {
            return false;
        }

        // The overtaken waiter goes back on the books as debt, which sends the release of this
        // acquisition down the slow path, where it finds the waiter.
        waiter.NoteOvertaken();
        waiters.Overtaken = waiter;
        Interlocked.Decrement(ref _count);

        return true;
    }

    /// <summary>Called by a blocking wait that is about to spin, see <see cref="_spinnerCredit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteSpinner()
    {
        // Test before writing so a gate that is already topped up costs a read on a contended line.
        if (_spinnerCredit < SpinnerCredit)
        {
            _spinnerCredit = SpinnerCredit;
        }
    }

    private ValueTask<AsyncSemaphoreReleaser> EnqueueWaiter(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (TryOvertake())
        {
            return new ValueTask<AsyncSemaphoreReleaser>(new AsyncSemaphoreReleaser(this));
        }

        var waiter = CommitWaiter(timeout, cancellationToken, synchronous: false, out var version);

        return waiter is null
            ? new ValueTask<AsyncSemaphoreReleaser>(new AsyncSemaphoreReleaser(this))
            : new ValueTask<AsyncSemaphoreReleaser>(waiter, version);
    }

    /// <summary>
    /// The zero-timeout check lives out here, not in the inlined caller: with a third return there the
    /// JIT stops keeping the caller's ValueTask in registers and copies it as a block, and reading the
    /// whole struct back right after its fields were stored one by one stalls on store forwarding.
    /// </summary>
    private ValueTask<AsyncSemaphoreReleaser> EnqueueTimedWaiter(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout == TimeSpan.Zero)
        {
            // A zero timeout is a single attempt: fail here without renting a node, arming a timer,
            // or creating waiter debt that a concurrent releaser would have to spin on and settle.
            return TimedOut(timeout);
        }

        return EnqueueWaiter(timeout, cancellationToken);
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
    private AsyncSemaphoreReleaser WaitBlocking(TimeSpan timeout, CancellationToken cancellationToken)
    {
        NoteSpinner();

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
            if (TryAcquireFast() || TryOvertake())
            {
                return _unpaired ? default : new AsyncSemaphoreReleaser(this);
            }
        }

        var waiter = CommitWaiter(timeout, cancellationToken, synchronous: true, out var version);

        if (waiter is null)
        {
            return _unpaired ? default : new AsyncSemaphoreReleaser(this);
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

        return OverflowPool.TryRent() ?? new Waiter();
    }

    private void ReturnWaiter(Waiter waiter)
    {
        // A node at rest never points back at a semaphore. The thread-local cache and the overflow
        // pool outlive this one and would root it. The instance slot would not, but a pooled node is
        // usually in an older generation than a short-lived gate, and a reference from there keeps
        // the dead gate and its queue alive until that generation is collected.
        waiter.ClearOwner();

        if (t_pooledWaiter is null)
        {
            t_pooledWaiter = waiter;

            return;
        }

        // Test before the CAS so a full slot costs a read, not a failed locked write.
        if (Volatile.Read(ref _pooledWaiter) is not null
            || Interlocked.CompareExchange(ref _pooledWaiter, waiter, null) is not null)
        {
            OverflowPool.Return(waiter);
        }
    }

    /// <summary>
    /// Publishes the waiter queue with a CAS so racing creators all end up on the same instance. The
    /// field is written exactly once, so plain reads elsewhere are safe: a stale null only lands back here.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WaiterQueue CreateQueue(ref WaiterQueue? location)
    {
        var created = new WaiterQueue();

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
        throw new ObjectDisposedException(nameof(AsyncSemaphore));
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
    private static ValueTask<AsyncSemaphoreReleaser> TimedOut(TimeSpan timeout)
    {
        return new ValueTask<AsyncSemaphoreReleaser>(Task.FromException<AsyncSemaphoreReleaser>(CreateTimeoutException(timeout)));
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

    /// <summary>
    /// Process-wide overflow behind the thread-local and per-instance slots. A pooled node keeps
    /// nothing from its last rental, so it can serve any semaphore. Sharing the overflow means a gate
    /// never builds a queue of its own for it (~840 B, which a short-lived gate paid on its first
    /// contention), and a node parked here outlives the gate that returned it instead of being
    /// collected with it.
    /// </summary>
    private static class OverflowPool
    {
        private static readonly ConcurrentQueue<Waiter> Nodes = new();

        /// <summary>Raised before an enqueue and lowered after a dequeue, so it never undercounts.</summary>
        private static int s_retained;

        public static int Count => Nodes.Count;

        public static Waiter? TryRent()
        {
            // Test before dequeuing so an empty pool costs a read.
            if (Volatile.Read(ref s_retained) > 0 && Nodes.TryDequeue(out var waiter))
            {
                Interlocked.Decrement(ref s_retained);

                return waiter;
            }

            return null;
        }

        public static void Return(Waiter waiter)
        {
            // Test before reserving so a full pool costs a read. Past the cap the node is dropped.
            if (Volatile.Read(ref s_retained) >= OverflowPoolCapacity)
            {
                return;
            }

            if (Interlocked.Increment(ref s_retained) > OverflowPoolCapacity)
            {
                Interlocked.Decrement(ref s_retained);

                return;
            }

            Nodes.Enqueue(waiter);
        }
    }

    /// <summary>
    /// The waiter queue, together with the two slots of a grant that can be overtaken. They live here
    /// and not on the gate, so a gate that never contends does not pay for them.
    /// </summary>
    private sealed class WaiterQueue : ConcurrentQueue<Waiter>
    {
        /// <summary>
        /// The waiter an exclusive gate's permit was granted to, until the hop that resumes it starts.
        /// Emptied by exactly one of that hop and <see cref="TryOvertake"/>, whichever exchanges first.
        /// It only ever names a waiter whose grant is pending on this gate, which is why a hop that
        /// outlived its own grant (see <see cref="Waiter.CollectGrant"/>) can still trust it.
        /// </summary>
        public Waiter? InFlight;

        /// <summary>The waiter whose grant was overtaken. Written and read only by whoever holds the permit.</summary>
        public Waiter? Overtaken;
    }

#if NETSTANDARD2_0
    private sealed class Waiter : IValueTaskSource<AsyncSemaphoreReleaser>, IValueTaskSource
#else
    private sealed class Waiter : IValueTaskSource<AsyncSemaphoreReleaser>, IValueTaskSource, IThreadPoolWorkItem
#endif
    {
        private const int StatePending = 0;
        private const int StateClaimed = 1;
        private const int StateCancelled = 2;

        private static readonly TimerCallback TimeoutCallback = static state => OnTimeout((Waiter)state!);
        private static readonly Action<object?> CancellationCallback = static state => OnCancelled((Waiter)state!);
        private static readonly Action<object?> WakeCallback = static state => ((ManualResetEventSlim)state!).Set();
#if NETSTANDARD2_0
        private static readonly WaitCallback GrantCallback = static state => ((Waiter)state!).CollectGrant();
#endif

        private AsyncSemaphore _owner = null!;

        private ManualResetValueTaskSourceCore<AsyncSemaphoreReleaser> _core;
        private int _state;
        private bool _cancellable;

        /// <summary>Set once a continuation is registered that the core would send to the thread pool.</summary>
        private bool _resumesOnThreadPool;

        /// <summary>Times a grant to this waiter was overtaken. Touched only by whoever holds the permit at the time.</summary>
        private byte _overtakes;

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
        public void SetOwner(AsyncSemaphore owner)
        {
            _owner = owner;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearOwner()
        {
            _owner = null!;
        }

        /// <summary>
        /// Whether a grant to this waiter may be published for <see cref="TryOvertake"/>.
        /// <para>
        /// Not a waiter that can still be cancelled or time out: claiming it ends that, and an overtaken
        /// grant would then sit out the overtaker's whole critical section with its timeout switched off.
        /// That also rules out a blocking waiter, which is woken inline anyway.
        /// </para>
        /// And only a waiter whose continuation is registered and bound for the thread pool, since the
        /// published grant is resumed from a hop of its own. Without a continuation there is nothing to
        /// hop for: completing on the spot costs nothing and keeps <c>IsCompleted</c> true as soon as the
        /// release returns. A continuation bound for a captured context would pay the hop on top of its
        /// own post. The flag is read without synchronization; a stale false only takes the direct path.
        /// <para>
        /// And not for ever: past <see cref="MaxOvertakes"/> the waiter is handed the permit directly, which
        /// nothing can overtake.
        /// </para>
        /// </summary>
        public bool CanBeOvertaken
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !_cancellable && _resumesOnThreadPool && _overtakes < MaxOvertakes;
        }

        public void NoteOvertaken()
        {
            _overtakes++;
        }

        /// <summary>Queues the hop that resumes a published grant.</summary>
        public void QueueGrant()
        {
#if NETSTANDARD2_0
            ThreadPool.UnsafeQueueUserWorkItem(GrantCallback, this);
#else
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
#endif
        }

#if !NETSTANDARD2_0
        void IThreadPoolWorkItem.Execute()
        {
            CollectGrant();
        }
#endif

        /// <summary>
        /// The hop of a published grant: resumes the waiter here, unless the grant was overtaken first.
        /// <para>
        /// A hop can outlive its grant. When the grant is overtaken the waiter is served by a later
        /// release, recycled, and possibly granted again before this runs. So it takes nothing from its
        /// own past: it asks the gate the node belongs to now whether this node's grant is pending there,
        /// and winning that exchange is a valid claim whichever release published it. The hop that was
        /// queued for that grant then loses the exchange and does nothing. A node at rest has no owner.
        /// </para>
        /// </summary>
        private void CollectGrant()
        {
            var waiters = _owner?._waiters;

            if (waiters is null || Interlocked.CompareExchange(ref waiters.InFlight, null, this) != this)
            {
                return;
            }

            // Already on the thread pool, so the continuation runs right here. GetResult restores the flag.
            _core.RunContinuationsAsynchronously = false;
            SetAcquired();
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

            _core.SetResult(owner._unpaired ? default : new AsyncSemaphoreReleaser(owner));
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
        public AsyncSemaphoreReleaser WaitSynchronously(TimeSpan timeout, int millisecondsTimeout, CancellationToken cancellationToken, short token)
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

            // The completing thread has already set the event, so it can be re-armed before GetResult
            // recycles the node.
            wakeEvent.Reset();

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

        public AsyncSemaphoreReleaser GetResult(short token)
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

            // A blocking wait and the hop of a published grant both complete the core inline. The
            // completing thread has read the flag by now, so the node goes back to its pooled shape.
            _core.RunContinuationsAsynchronously = true;
            _core.Reset();
            _resumesOnThreadPool = false;
            _overtakes = 0;

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
            // What the core looks at before it captures a context to resume on. Any context at all counts
            // here, which errs towards the direct handoff.
            if ((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) == 0
                || (SynchronizationContext.Current is null && ReferenceEquals(TaskScheduler.Current, TaskScheduler.Default)))
            {
                _resumesOnThreadPool = true;
            }

            _core.OnCompleted(continuation, state, token, flags);
        }
    }
}
