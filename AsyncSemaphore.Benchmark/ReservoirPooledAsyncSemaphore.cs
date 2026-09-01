using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Reservoir;

namespace AsyncSemaphore.Benchmark;

/// <summary>
/// Benchmark-only copy of <see cref="Semaphores.AsyncSemaphore"/> with the hand-rolled
/// three-tier waiter pool replaced by a Reservoir <see cref="ObjectPool{T, TPolicy}"/>
/// (struct policy, thread-local fast path enabled). Everything else is identical so the
/// handoff benchmark isolates the pooling strategy.
/// </summary>
public sealed class ReservoirPooledAsyncSemaphore
{
    private int _count;

    private readonly ConcurrentQueue<Waiter> _waiters = new();
    private readonly ObjectPool<Waiter, WaiterPolicy> _nodePool = new(default, 128, threadLocalFastPath: true);

    public ReservoirPooledAsyncSemaphore(int maxCount)
    {
        _count = maxCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<Releaser> WaitAsync()
    {
        if (TryAcquireFast())
        {
            return new ValueTask<Releaser>(new Releaser(this));
        }

        return EnqueueWaiter();
    }

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

    internal void Release()
    {
        if (Interlocked.Increment(ref _count) <= 0)
        {
            ReleaseNextWaiter();
        }
    }

    private void ReleaseNextWaiter()
    {
        while (true)
        {
            Waiter? waiter;
            var spinner = default(SpinWait);

            while (!_waiters.TryDequeue(out waiter))
            {
                spinner.SpinOnce();
            }

            if (waiter.TryClaim())
            {
                waiter.SetAcquired();
                return;
            }

            if (Interlocked.Increment(ref _count) > 0)
            {
                return;
            }
        }
    }

    private ValueTask<Releaser> EnqueueWaiter()
    {
        var waiter = _nodePool.Rent();

        waiter.SetOwner(this);

        var version = waiter.Version;

        if (Interlocked.Decrement(ref _count) >= 0)
        {
            ReturnWaiter(waiter);

            return new ValueTask<Releaser>(new Releaser(this));
        }

        _waiters.Enqueue(waiter);

        return new ValueTask<Releaser>(waiter, version);
    }

    private void ReturnWaiter(Waiter waiter)
    {
        _nodePool.Return(waiter);
    }

    public struct Releaser : IDisposable
    {
        private ReservoirPooledAsyncSemaphore? _semaphore;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Releaser(ReservoirPooledAsyncSemaphore semaphore)
        {
            _semaphore = semaphore;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }

    internal struct WaiterPolicy : IPooledObjectPolicy<Waiter>
    {
        public Waiter Create() => new();

        public bool TryReset(Waiter obj) => true;

        public void Destroy(Waiter obj)
        {
        }
    }

    internal sealed class Waiter : IValueTaskSource<Releaser>
    {
        private ReservoirPooledAsyncSemaphore _owner = null!;

        private ManualResetValueTaskSourceCore<Releaser> _core;

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
        public void SetOwner(ReservoirPooledAsyncSemaphore owner)
        {
            _owner = owner;
        }

        // The benchmark only exercises non-cancellable waits, matching the production
        // fast path where TryClaim short-circuits without an interlocked operation.
        public bool TryClaim() => true;

        public void SetAcquired()
        {
            _core.SetResult(new Releaser(_owner));
        }

        public Releaser GetResult(short token)
        {
            var result = _core.GetResult(token);

            _core.Reset();

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
