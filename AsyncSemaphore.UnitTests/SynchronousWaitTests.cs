// These tests split acquire from release and defer awaits to race the blocking path against
// the asynchronous one, which is exactly what the usage analyzers guard against.
#pragma warning disable SEM0001, SEM0002, SEM0003, SEM0004

using TUnit.Assertions.Enums;

namespace AsyncSemaphore.UnitTests;

/// <summary>
/// <c>TryWait</c> and the blocking <c>Wait</c>. Blocking waits run on dedicated threads so they
/// never starve the thread pool the rest of the suite runs on.
/// </summary>
public class SynchronousWaitTests
{
    private static readonly TimeSpan StressTimeout = TimeSpan.FromSeconds(60);

    [Test]
    public async Task TryWait_Takes_A_Permit_When_One_Is_Available()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(2);

        var acquired = semaphore.TryWait(out var releaser);

        await Assert.That(acquired).IsTrue();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        releaser.Dispose();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(2);
    }

    [Test]
    public async Task TryWait_Fails_Without_Queueing_When_Held()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();

        var acquired = semaphore.TryWait(out var releaser);

        await Assert.That(acquired).IsFalse();

        // The failed attempt's handle releases nothing
        releaser.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);

        // A queued attempt would have left waiter debt that swallows this release
        holder.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task TryWait_Throws_After_Dispose()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);
        semaphore.Dispose();

        await Assert.That(() => semaphore.TryWait(out _)).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task Wait_Acquires_Immediately_When_A_Permit_Is_Available()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        using (semaphore.Wait())
        {
            await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        }

        using (semaphore.Wait(TimeSpan.Zero))
        {
            await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Wait_Blocks_Until_The_Permit_Is_Released()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();
        var acquired = 0;

        var blocked = BlockingThread.Start(() =>
        {
            using var @lock = semaphore.Wait();
            Volatile.Write(ref acquired, 1);
        });

        await blocked.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);
        await Assert.That(Volatile.Read(ref acquired)).IsEqualTo(0);

        holder.Dispose();
        await blocked.Completion;

        await Assert.That(Volatile.Read(ref acquired)).IsEqualTo(1);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Wait_Timeout_Throws_TimeoutException_And_Keeps_The_Count()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        using (await semaphore.WaitAsync())
        {
            await Assert.That(() => semaphore.Wait(TimeSpan.FromMilliseconds(50)))
                .ThrowsExactly<TimeoutException>();
        }

        // The timed-out node is still queued; the release above must have settled it
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        using var @lock = semaphore.Wait(TimeSpan.FromMilliseconds(50));
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(100, 0, 100)]
    [Arguments(100, 30, 70)]
    [Arguments(100, 100, 0)]
    [Arguments(100, 5_000, 0)]
    [Arguments(int.MaxValue, 1, int.MaxValue - 1)]
    public async Task Time_Spent_Before_Parking_Comes_Out_Of_The_Timeout(int timeout, int elapsed, int expected)
    {
        var elapsedTicks = elapsed * System.Diagnostics.Stopwatch.Frequency / 1000;

        await Assert.That(Semaphores.AsyncSemaphore.RemainingMilliseconds(timeout, elapsedTicks)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_Partial_Millisecond_Before_Parking_Never_Shortens_The_Timeout()
    {
        // Rounding the elapsed time up would let a wait time out before its budget is spent
        var justUnderOneMillisecond = (System.Diagnostics.Stopwatch.Frequency / 1000) - 1;

        await Assert.That(Semaphores.AsyncSemaphore.RemainingMilliseconds(100, justUnderOneMillisecond)).IsEqualTo(100);
    }

    [Test]
    public async Task Wait_Zero_Timeout_Throws_Immediately_When_Held()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();

        await Assert.That(() => semaphore.Wait(TimeSpan.Zero)).ThrowsExactly<TimeoutException>();

        // A single attempt never queues, so it leaves no debt behind
        holder.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Wait_Cancellation_Throws_OperationCanceledException_And_Keeps_The_Count()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        using (await semaphore.WaitAsync())
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.That(() => semaphore.Wait(cts.Token)).ThrowsExactly<OperationCanceledException>();
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Wait_With_A_PreCancelled_Token_Throws_Without_Touching_The_Count()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.That(() => semaphore.Wait(cts.Token)).ThrowsExactly<OperationCanceledException>();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Interrupted_Wait_Surfaces_The_Interrupt_Without_Leaking_A_Permit()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();

        // No timeout and no token: the interrupt is the only thing that can abandon this wait
        var blocked = BlockingThread.Start(() =>
        {
            using var @lock = semaphore.Wait();
        });

        await blocked.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);
        blocked.Interrupt();

        await Assert.That(async () => await blocked.Completion).ThrowsExactly<ThreadInterruptedException>();

        // The abandoned node is still queued; handing it the permit would lose the permit for good
        holder.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Interrupt_That_Loses_To_A_Release_Keeps_The_Permit_And_Stays_Pending()
    {
        const int maxAttempts = 500;
        const int lostInterruptsWanted = 20;

        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var lostInterrupts = 0;

        for (var attempt = 0; attempt < maxAttempts && lostInterrupts < lostInterruptsWanted; attempt++)
        {
            var holder = await semaphore.WaitAsync();
            var acquired = false;
            var interruptStillPending = false;

            var blocked = BlockingThread.Start(() =>
            {
                Semaphores.AsyncSemaphoreReleaser @lock;

                try
                {
                    @lock = semaphore.Wait();
                }
                catch (ThreadInterruptedException)
                {
                    // The interrupt won the node; the release below has to settle it as a dead entry
                    return;
                }

                acquired = true;

                try
                {
                    // The release won the node, so the interrupt must surface at the next blocking call
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                }
                catch (ThreadInterruptedException)
                {
                    interruptStillPending = true;
                }
                finally
                {
                    @lock.Dispose();
                }
            });

            await blocked.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);

            // Interrupt first, release right behind it from the same thread. The claim is a few
            // instructions away while the interrupted thread still has to be scheduled, so the claim
            // usually wins the node and the interrupt arrives to find it already taken
            blocked.Interrupt();
            holder.Dispose();

            await WhenAllWithTimeout([blocked.Completion]);

            if (acquired)
            {
                lostInterrupts++;

                await Assert.That(interruptStillPending).IsTrue();
            }

            // Whoever won, there is exactly one permit and no debt
            await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(0);
            await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
        }

        await Assert.That(lostInterrupts).IsGreaterThan(0);
    }

    [Test]
    public async Task Wait_Rejects_An_Out_Of_Range_Timeout()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        await Assert.That(() => semaphore.Wait(TimeSpan.FromMilliseconds(-2)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Wait_Throws_After_Dispose()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);
        semaphore.Dispose();

        await Assert.That(() => semaphore.Wait()).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task Blocking_And_Async_Waiters_Share_One_Fifo_Queue()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();
        var completionOrder = new List<string>();

        var first = BlockingThread.Start(() =>
        {
            using var @lock = semaphore.Wait();
            Record("blocking-1");
        });

        await first.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);

        // WaitAsync enqueues synchronously before returning, so it is queued behind the blocked thread
        var second = Consume(semaphore.WaitAsync(), "async-2");

        var third = BlockingThread.Start(() =>
        {
            using var @lock = semaphore.Wait();
            Record("blocking-3");
        });

        await third.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 3);

        holder.Dispose();

        await WhenAllWithTimeout([first.Completion, second, third.Completion]);

        await Assert.That(completionOrder).IsEquivalentTo(["blocking-1", "async-2", "blocking-3"], CollectionOrdering.Matching);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        async Task Consume(ValueTask<Semaphores.AsyncSemaphoreReleaser> pending, string name)
        {
            using var @lock = await pending;
            Record(name);
        }

        void Record(string name)
        {
            lock (completionOrder)
            {
                completionOrder.Add(name);
            }
        }
    }

    [Test]
    public async Task Blocking_And_Async_Waiters_Never_Violate_Mutual_Exclusion()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var inCriticalSection = 0;
        var maxObserved = 0;
        long sharedCounter = 0;

        const int workersPerFlavor = 4;
        const int iterationsPerWorker = 5_000;

        var blockingWorkers = Enumerable.Range(0, workersPerFlavor).Select(_ => BlockingThread.Start(() =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                using var @lock = semaphore.Wait();
                CriticalSection();
            }
        }).Completion);

        var asyncWorkers = Enumerable.Range(0, workersPerFlavor).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                using var @lock = await semaphore.WaitAsync();
                CriticalSection();
            }
        }));

        await WhenAllWithTimeout(blockingWorkers.Concat(asyncWorkers).ToArray());

        await Assert.That(maxObserved).IsEqualTo(1);
        await Assert.That(sharedCounter).IsEqualTo(2L * workersPerFlavor * iterationsPerWorker);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        void CriticalSection()
        {
            var current = Interlocked.Increment(ref inCriticalSection);
            InterlockedMax(ref maxObserved, current);
            sharedCounter++; // unsynchronized on purpose; semaphore is the only guard
            Interlocked.Decrement(ref inCriticalSection);
        }
    }

    [Test]
    public async Task Two_Blocking_Contenders_With_Varied_Holds_Lose_No_Release_At_The_Commit()
    {
        const int contenders = 2;
        const int iterationsPerContender = 4_000;

        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var inCriticalSection = 0;
        var maxObserved = 0;
        long sharedCounter = 0;

        // Exactly two contenders keep the count at zero with no debt while one holds, and holds that
        // straddle the other's spin budget land releases around its commit: just before its last spin
        // (taken on the fast path), between that spin and the commit decrement (the decrement itself
        // finds the permit), and just after (handed over through the queue)
        var tasks = Enumerable.Range(0, contenders).Select(contender => BlockingThread.Start(() =>
        {
            var random = new Random(contender * 7919);

            for (var i = 0; i < iterationsPerContender; i++)
            {
                using var @lock = semaphore.Wait();

                var current = Interlocked.Increment(ref inCriticalSection);
                InterlockedMax(ref maxObserved, current);
                sharedCounter++; // unsynchronized on purpose; semaphore is the only guard
                Thread.SpinWait(random.Next(0, 20_000));
                Interlocked.Decrement(ref inCriticalSection);
            }
        }).Completion).ToArray();

        await WhenAllWithTimeout(tasks);

        await Assert.That(maxObserved).IsEqualTo(1);
        await Assert.That(sharedCounter).IsEqualTo((long)contenders * iterationsPerContender);
        await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(0);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Blocking_Timeout_Storm_Accounts_For_Every_Wait()
    {
        const int maxCount = 2;
        const int workers = 8;
        const int iterationsPerWorker = 300;

        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);

        var inCriticalSection = 0;
        var maxObserved = 0;
        var acquired = 0;
        var timedOut = 0;

        // Timeouts of 0-2 ms against ~1 ms holds keep the self-timeout racing the claim
        var tasks = Enumerable.Range(0, workers).Select(worker => BlockingThread.Start(() =>
        {
            var random = new Random(worker);

            for (var i = 0; i < iterationsPerWorker; i++)
            {
                try
                {
                    using var @lock = semaphore.Wait(TimeSpan.FromMilliseconds(random.Next(0, 3)));

                    var current = Interlocked.Increment(ref inCriticalSection);
                    InterlockedMax(ref maxObserved, current);
                    Thread.Sleep(1);
                    Interlocked.Decrement(ref inCriticalSection);
                    Interlocked.Increment(ref acquired);
                }
                catch (TimeoutException)
                {
                    Interlocked.Increment(ref timedOut);
                }
            }
        }).Completion).ToArray();

        await WhenAllWithTimeout(tasks);

        await Assert.That(maxObserved).IsLessThanOrEqualTo(maxCount);
        await Assert.That(acquired + timedOut).IsEqualTo(workers * iterationsPerWorker);

        // Every timed-out node is still queued as a dead entry; the permits must all still be there
        await AssertAllPermitsAvailable(semaphore, maxCount);
    }

    [Test]
    public async Task Blocking_Cancellation_Racing_Release_Loses_No_Permits()
    {
        const int iterations = 100;

        using var semaphore = new Semaphores.AsyncSemaphore(1);

        for (var i = 0; i < iterations; i++)
        {
            var holder = await semaphore.WaitAsync();
            using var cts = new CancellationTokenSource();

            var blocked = BlockingThread.Start(() =>
            {
                try
                {
                    using var @lock = semaphore.Wait(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Either outcome is fine; the count below is what must hold
                }
            });

            await blocked.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);

            // Cancel and release at the same instant, from two threads released together, so the
            // self-cancel CAS and the releaser's claim each get to win some of the iterations
            using var start = new Barrier(2);

            var cancel = Task.Run(() =>
            {
                start.SignalAndWait();
                cts.Cancel();
            });

            var release = Task.Run(() =>
            {
                start.SignalAndWait();
                holder.Dispose();
            });

            await WhenAllWithTimeout([cancel, release, blocked.Completion]);

            // A cancelled node stays queued until the next release settles it
            using (await semaphore.WaitAsync())
            {
            }

            await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Node_Recycled_From_A_Blocking_Wait_Resumes_Async_Waiters_Off_The_Releasing_Thread()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();
        var releasingThreadId = 0;
        var continuationThreadId = 0;
        var continuationRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocked = BlockingThread.Start(() =>
        {
            // The contended blocking wait parks its node in this thread's node cache when it returns
            var mine = semaphore.Wait();

            // Contended against ourselves, so this wait rents that same node straight back
            var awaiter = semaphore.WaitAsync().GetAwaiter();

            awaiter.OnCompleted(() =>
            {
                Volatile.Write(ref continuationThreadId, Environment.CurrentManagedThreadId);
                awaiter.GetResult().Dispose();
                continuationRan.TrySetResult(true);
            });

            releasingThreadId = Environment.CurrentManagedThreadId;
            mine.Dispose();
        });

        await blocked.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);
        holder.Dispose();

        await WhenAllWithTimeout([blocked.Completion, continuationRan.Task]);

        // A blocking wait wakes its thread inline; that setting must not leak into the pooled node,
        // or an async waiter's continuation would run inside the releaser's Dispose call
        await Assert.That(continuationThreadId).IsNotEqualTo(releasingThreadId);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    private static async Task AssertAllPermitsAvailable(Semaphores.AsyncSemaphore semaphore, int maxCount)
    {
        var holders = new Semaphores.AsyncSemaphoreReleaser[maxCount];

        for (var i = 0; i < maxCount; i++)
        {
            holders[i] = await semaphore.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);

        foreach (var holder in holders)
        {
            holder.Dispose();
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(maxCount);
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
}
