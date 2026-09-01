// These tests deliberately defer awaits and split acquire from release to race the
// semaphore's internals, which is exactly what the usage analyzers guard against.
#pragma warning disable SEM0001, SEM0002, SEM0003

using TUnit.Assertions.Enums;

namespace AsyncSemaphore.UnitTests;

/// <summary>
/// Stress tests targeting the lock-free core: the fast-path CAS, the decrement-to-enqueue
/// commit window, the claim/cancel CAS, dead-node settlement, and the waiter node pools.
/// </summary>
public class ContentionTests
{
    private static readonly TimeSpan StressTimeout = TimeSpan.FromSeconds(60);

    [Test]
    public async Task Queued_Waiters_Are_Released_In_Fifo_Order()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();

        var completionOrder = new List<int>();
        var waiters = new List<Task>();

        // WaitAsync enqueues synchronously before returning, so call order == queue order
        for (var i = 0; i < 10; i++)
        {
            waiters.Add(Consume(semaphore.WaitAsync(), i));
        }

        await Task.Run(holder.Dispose);

        await WhenAllWithTimeout(waiters);

        await Assert.That(completionOrder).IsEquivalentTo(Enumerable.Range(0, 10).ToList(), CollectionOrdering.Matching);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        async Task Consume(ValueTask<Semaphores.AsyncSemaphoreReleaser> pending, int index)
        {
            using var @lock = await pending;

            lock (completionOrder)
            {
                completionOrder.Add(index);
            }
        }
    }

    [Test]
    public async Task Sync_Hot_Loop_Never_Violates_Mutual_Exclusion()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var inCriticalSection = 0;
        var maxObserved = 0;
        long sharedCounter = 0;

        const int workers = 8;
        const int iterationsPerWorker = 20_000;

        // No awaits inside the critical section: hammers the fast-path CAS and the
        // synchronous release/handoff transitions as hard as possible
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                using var @lock = await semaphore.WaitAsync();

                var current = Interlocked.Increment(ref inCriticalSection);
                InterlockedMax(ref maxObserved, current);
                sharedCounter++; // unsynchronized on purpose; semaphore is the only guard
                Interlocked.Decrement(ref inCriticalSection);
            }
        })).ToArray();

        await WhenAllWithTimeout(tasks);

        await Assert.That(maxObserved).IsEqualTo(1);
        await Assert.That(sharedCounter).IsEqualTo((long)workers * iterationsPerWorker);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    [MethodDataSource(nameof(PermitCounts))]
    public async Task Permit_Limit_Holds_For_All_Max_Counts(int maxCount)
    {
        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);

        var holders = 0;
        var maxObserved = 0;
        var completed = 0;

        const int workers = 12;
        const int iterationsPerWorker = 2_000;

        var tasks = Enumerable.Range(0, workers).Select(workerIndex => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                using var @lock = await semaphore.WaitAsync();

                var current = Interlocked.Increment(ref holders);
                InterlockedMax(ref maxObserved, current);

                // Alternate sync and async holds so both completion paths are exercised
                if ((workerIndex + i) % 2 == 0)
                {
                    await Task.Yield();
                }

                Interlocked.Decrement(ref holders);
                Interlocked.Increment(ref completed);
            }
        })).ToArray();

        await WhenAllWithTimeout(tasks);

        await Assert.That(maxObserved).IsLessThanOrEqualTo(maxCount);
        await Assert.That(completed).IsEqualTo(workers * iterationsPerWorker);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(maxCount);
    }

    [Test]
    public async Task Mixed_Wait_Flavors_Contend_Without_Corruption()
    {
        const int maxCount = 3;
        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);

        var holders = 0;
        var maxObserved = 0;
        var completed = 0;

        const int workers = 9;
        const int iterationsPerWorker = 2_000;

        using var neverCancelled = new CancellationTokenSource();

        // Hold every permit so each worker's first wait of every flavor is guaranteed to queue
        var initialHolders = await HoldAllPermits(semaphore, maxCount);
        var allQueued = new QueuedGate(workers);

        // Each flavor takes a different slow path: non-cancellable (skips the claim CAS),
        // token-armed (claim CAS + registration), and timer-armed (claim CAS + timer)
        var tasks = Enumerable.Range(0, workers).Select(workerIndex => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                var pending = (workerIndex % 3) switch
                {
                    0 => semaphore.WaitAsync(),
                    1 => semaphore.WaitAsync(neverCancelled.Token),
                    _ => semaphore.WaitAsync(TimeSpan.FromMinutes(5)),
                };

                if (i == 0)
                {
                    allQueued.Signal();
                }

                using var @lock = await pending;

                var current = Interlocked.Increment(ref holders);
                InterlockedMax(ref maxObserved, current);
                Interlocked.Decrement(ref holders);
                Interlocked.Increment(ref completed);
            }
        })).ToArray();

        await allQueued.AllQueued;
        ReleaseAll(initialHolders);

        await WhenAllWithTimeout(tasks);

        await Assert.That(maxObserved).IsLessThanOrEqualTo(maxCount);
        await Assert.That(completed).IsEqualTo(workers * iterationsPerWorker);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(maxCount);
    }

    [Test]
    public async Task Random_Cancellation_Under_Contention_Accounts_For_Every_Wait()
    {
        const int maxCount = 2;
        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);

        var acquired = 0;
        var cancelled = 0;
        var holders = 0;
        var maxObserved = 0;

        const int workers = 8;
        const int iterationsPerWorker = 1_000;

        var tasks = Enumerable.Range(0, workers).Select(workerIndex => Task.Run(async () =>
        {
            var random = new Random(workerIndex * 7919);

            for (var i = 0; i < iterationsPerWorker; i++)
            {
                using var cts = new CancellationTokenSource();

                var pending = semaphore.WaitAsync(cts.Token);

                // Cancel concurrently on some iterations so the claim/cancel CAS races both ways
                var cancelTask = random.Next(3) == 0 ? Task.Run(cts.Cancel) : Task.CompletedTask;

                try
                {
                    using (await pending)
                    {
                        var current = Interlocked.Increment(ref holders);
                        InterlockedMax(ref maxObserved, current);
                        Interlocked.Decrement(ref holders);
                        Interlocked.Increment(ref acquired);
                    }
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                }

                await cancelTask;
            }
        })).ToArray();

        await WhenAllWithTimeout(tasks);

        // Every wait resolved exactly one way, and no permit was lost or duplicated
        await Assert.That(acquired + cancelled).IsEqualTo(workers * iterationsPerWorker);
        await Assert.That(maxObserved).IsLessThanOrEqualTo(maxCount);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(maxCount);

        using var final = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(maxCount - 1);
    }

    [Test]
    public async Task Timeout_Storm_Under_Contention_Accounts_For_Every_Wait()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var acquired = 0;
        var timedOut = 0;

        const int workers = 8;
        const int iterationsPerWorker = 300;

        var tasks = Enumerable.Range(0, workers).Select(workerIndex => Task.Run(async () =>
        {
            var random = new Random(workerIndex * 104729);

            for (var i = 0; i < iterationsPerWorker; i++)
            {
                try
                {
                    using (await semaphore.WaitAsync(TimeSpan.FromMilliseconds(random.Next(0, 20))))
                    {
                        Interlocked.Increment(ref acquired);

                        if (random.Next(2) == 0)
                        {
                            await Task.Yield();
                        }
                    }
                }
                catch (TimeoutException)
                {
                    Interlocked.Increment(ref timedOut);
                }
            }
        })).ToArray();

        await WhenAllWithTimeout(tasks);

        await Assert.That(acquired + timedOut).IsEqualTo(workers * iterationsPerWorker);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        using var final = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Mass_Waiter_Queue_Drains_Completely_From_Single_Release()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();

        var completed = 0;
        const int waiters = 1_000;
        const int spawners = 8;

        // Enqueue from many threads at once so waiters land in the queue while
        // release handoffs are already chaining through it
        var spawnerTasks = Enumerable.Range(0, spawners).Select(_ => Task.Run(async () =>
        {
            var localWaiters = new Task[waiters / spawners];

            for (var i = 0; i < localWaiters.Length; i++)
            {
                localWaiters[i] = Consume();
            }

            await Task.WhenAll(localWaiters);
        })).ToArray();

        await Task.Run(holder.Dispose);

        await WhenAllWithTimeout(spawnerTasks);

        await Assert.That(completed).IsEqualTo(waiters);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        async Task Consume()
        {
            using var @lock = await semaphore.WaitAsync();

            Interlocked.Increment(ref completed);
        }
    }

    [Test]
    public async Task Cancelled_Waiters_Interleaved_With_Live_Waiters_Do_Not_Steal_Or_Block()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();

        using var cts = new CancellationTokenSource();

        // Interleave live and doomed waiters in the queue: L D L D L D
        var liveWaiters = new List<Task>();
        var doomedWaiters = new List<Task>();
        var liveCompleted = 0;

        for (var i = 0; i < 3; i++)
        {
            liveWaiters.Add(ConsumeLive(semaphore.WaitAsync()));
            doomedWaiters.Add(ConsumeDoomed(semaphore.WaitAsync(cts.Token)));
        }

        cts.Cancel();
        await WhenAllWithTimeout(doomedWaiters);

        // The dead nodes are still queued; releasing must settle them and still reach every live waiter
        await Task.Run(holder.Dispose);

        await WhenAllWithTimeout(liveWaiters);

        await Assert.That(liveCompleted).IsEqualTo(3);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        async Task ConsumeLive(ValueTask<Semaphores.AsyncSemaphoreReleaser> pending)
        {
            using var @lock = await pending;

            Interlocked.Increment(ref liveCompleted);
        }

        async Task ConsumeDoomed(ValueTask<Semaphores.AsyncSemaphoreReleaser> pending)
        {
            try
            {
                using var @lock = await pending;
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }

    [Test]
    public async Task Cancellation_After_Acquisition_Is_A_NoOp()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        using var cts = new CancellationTokenSource();

        var holder = await semaphore.WaitAsync();

        // Force the slow path so the token is actually registered
        var pending = semaphore.WaitAsync(cts.Token);

        await Task.Run(holder.Dispose);

        using (await pending)
        {
            // The waiter has been claimed; the cancellation callback must lose the CAS
            cts.Cancel();

            await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        using var final = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Timeout_Firing_After_Acquisition_Is_A_NoOp()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();

        // Force the slow path so the timer is actually armed
        var pending = semaphore.WaitAsync(TimeSpan.FromMilliseconds(200));

        await Task.Run(holder.Dispose);

        using (await pending)
        {
            // Hold past the original timeout; the timer callback must lose the CAS
            await Task.Delay(400);

            await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Zero_Timeout_Acquires_When_Permit_Available()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        using (await semaphore.WaitAsync(TimeSpan.Zero))
        {
            await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task PreCancelled_Token_Throws_Synchronously_Without_Touching_The_Count()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A synchronous delegate: the throw must happen before any ValueTask is handed back
        await Assert.That(() => { _ = semaphore.WaitAsync(cts.Token); })
            .ThrowsExactly<OperationCanceledException>();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        using var @lock = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Double_Dispose_Of_Releaser_Releases_Exactly_Once()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var releaser = await semaphore.WaitAsync();

#pragma warning disable SEM0004 // deliberately violating the analyzer to prove single-release semantics
        releaser.Dispose();
        releaser.Dispose();
#pragma warning restore SEM0004

        // A double release would have pushed the count past maxCount
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        // And must not have fabricated a permit for a waiter
        using (await semaphore.WaitAsync())
        {
            await Assert.That(semaphore.CurrentCount).IsEqualTo(0);

            await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.FromMilliseconds(50)))
                .ThrowsExactly<TimeoutException>();
        }
    }

    [Test]
    public async Task Release_From_A_Different_Thread_Hands_Off_Correctly()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        const int iterations = 2_000;

        for (var i = 0; i < iterations; i++)
        {
            var releaser = await semaphore.WaitAsync();

            var pending = semaphore.WaitAsync();

            // Dispose on a foreign thread while this thread awaits the handoff
            var releaseTask = Task.Run(releaser.Dispose);

            using (await pending)
            {
                await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
            }

            await releaseTask;
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Two_Semaphores_On_Shared_Threads_Keep_Independent_Invariants()
    {
        // The thread-static waiter cache is shared across semaphores; nodes are re-owned
        // on rent, so interleaving two instances on the same threads must not cross wires
        using var semaphoreA = new Semaphores.AsyncSemaphore(1);
        using var semaphoreB = new Semaphores.AsyncSemaphore(1);

        var inA = 0;
        var inB = 0;
        var maxA = 0;
        var maxB = 0;
        var completed = 0;

        const int workers = 8;
        const int iterationsPerWorker = 3_000;

        // Hold both permits so every worker's first wait queues on A or B, then release
        // both at once so the two instances see overlapping waits from the same threads
        var holderA = await HoldAllPermits(semaphoreA, 1);
        var holderB = await HoldAllPermits(semaphoreB, 1);
        var allQueued = new QueuedGate(workers);

        var tasks = Enumerable.Range(0, workers).Select(workerIndex => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                var useA = (workerIndex + i) % 2 == 0;
                var pending = useA ? semaphoreA.WaitAsync() : semaphoreB.WaitAsync();

                if (i == 0)
                {
                    allQueued.Signal();
                }

                using var @lock = await pending;

                if (useA)
                {
                    var current = Interlocked.Increment(ref inA);
                    InterlockedMax(ref maxA, current);
                    Interlocked.Decrement(ref inA);
                }
                else
                {
                    var current = Interlocked.Increment(ref inB);
                    InterlockedMax(ref maxB, current);
                    Interlocked.Decrement(ref inB);
                }

                Interlocked.Increment(ref completed);
            }
        })).ToArray();

        await allQueued.AllQueued;
        ReleaseAll(holderA);
        ReleaseAll(holderB);

        await WhenAllWithTimeout(tasks);

        await Assert.That(maxA).IsEqualTo(1);
        await Assert.That(maxB).IsEqualTo(1);
        await Assert.That(completed).IsEqualTo(workers * iterationsPerWorker);
        await Assert.That(semaphoreA.CurrentCount).IsEqualTo(1);
        await Assert.That(semaphoreB.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Nested_Semaphores_Under_Contention_Do_Not_Corrupt_Each_Other()
    {
        using var outer = new Semaphores.AsyncSemaphore(2);
        using var inner = new Semaphores.AsyncSemaphore(1);

        var inInner = 0;
        var maxInner = 0;
        var completed = 0;

        const int workers = 6;
        const int iterationsPerWorker = 2_000;

        // Hold every outer and inner permit so all workers queue on the outer semaphore, then
        // release everything at once so the winners immediately contend on the inner one
        var outerHolders = await HoldAllPermits(outer, 2);
        var innerHolders = await HoldAllPermits(inner, 1);
        var allQueued = new QueuedGate(workers);

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                var pendingOuter = outer.WaitAsync();

                if (i == 0)
                {
                    allQueued.Signal();
                }

                using var outerLock = await pendingOuter;
                using var innerLock = await inner.WaitAsync();

                var current = Interlocked.Increment(ref inInner);
                InterlockedMax(ref maxInner, current);
                Interlocked.Decrement(ref inInner);
                Interlocked.Increment(ref completed);
            }
        })).ToArray();

        await allQueued.AllQueued;
        ReleaseAll(outerHolders);
        ReleaseAll(innerHolders);

        await WhenAllWithTimeout(tasks);

        await Assert.That(maxInner).IsEqualTo(1);
        await Assert.That(completed).IsEqualTo(workers * iterationsPerWorker);
        await Assert.That(outer.CurrentCount).IsEqualTo(2);
        await Assert.That(inner.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Cancel_Release_And_New_Waiters_All_Race_Without_Losing_Permits()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        const int iterations = 1_000;

        for (var i = 0; i < iterations; i++)
        {
            var holder = await semaphore.WaitAsync();

            using var cts = new CancellationTokenSource();

            var doomed = semaphore.WaitAsync(cts.Token);
            var live = semaphore.WaitAsync();

            // Three-way race: release the permit, cancel the first waiter, and have a
            // second live waiter queued behind the potentially-dead node
            var releaseTask = Task.Run(holder.Dispose);
            var cancelTask = Task.Run(cts.Cancel);

            var doomedAcquired = false;

            try
            {
                using (await doomed)
                {
                    doomedAcquired = true;
                }
            }
            catch (OperationCanceledException)
            {
                // cancellation won; the dead node must be settled without starving the live waiter
            }

            using (await live)
            {
            }

            await Task.WhenAll(releaseTask, cancelTask);

            _ = doomedAcquired;
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        using var final = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    public static IEnumerable<int> PermitCounts() => [1, 2, 4, 8];

    private static async Task<Semaphores.AsyncSemaphoreReleaser[]> HoldAllPermits(Semaphores.AsyncSemaphore semaphore, int maxCount)
    {
        var holders = new Semaphores.AsyncSemaphoreReleaser[maxCount];

        for (var i = 0; i < maxCount; i++)
        {
            holders[i] = await semaphore.WaitAsync();
        }

        return holders;
    }

    private static void ReleaseAll(Semaphores.AsyncSemaphoreReleaser[] holders)
    {
#pragma warning disable SEM0004 // the releasers were deliberately kept out of a using so the test controls the release moment
        for (var i = 0; i < holders.Length; i++)
        {
            holders[i].Dispose();
        }
#pragma warning restore SEM0004
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref location);
        }
        while (value > current && Interlocked.CompareExchange(ref location, value, current) != current);
    }

    private static async Task WhenAllWithTimeout(IEnumerable<Task> tasks)
    {
        var all = Task.WhenAll(tasks);
        var completedFirst = await Task.WhenAny(all, Task.Delay(StressTimeout));

        if (completedFirst != all)
        {
            throw new TimeoutException($"Stress test did not finish within {StressTimeout}; a waiter was likely lost.");
        }

        await all;
    }

    /// <summary>
    /// Completes once every worker has issued its first wait. Combined with pre-held permits this
    /// guarantees each worker actually queues instead of racing through the fast path unopposed.
    /// </summary>
    private sealed class QueuedGate
    {
        private readonly TaskCompletionSource<bool> _allQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining;

        public QueuedGate(int workers)
        {
            _remaining = workers;
        }

        public Task AllQueued => _allQueued.Task;

        public void Signal()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _allQueued.TrySetResult(true);
            }
        }
    }
}
