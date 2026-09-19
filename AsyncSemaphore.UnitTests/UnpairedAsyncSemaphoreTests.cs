namespace AsyncSemaphore.UnitTests;

/// <summary>
/// <see cref="Semaphores.UnpairedAsyncSemaphore"/>: waits that hand out no releaser, and releases
/// published by code that never waited.
/// </summary>
public class UnpairedAsyncSemaphoreTests
{
    private static readonly TimeSpan StressTimeout = TimeSpan.FromSeconds(60);

    [Test]
    public async Task Constructor_Rejects_A_Negative_Count()
    {
        await Assert.That(() => new Semaphores.UnpairedAsyncSemaphore(-1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_Accepts_A_Zero_Count()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        await Assert.That(semaphore.TryWait()).IsFalse();
    }

    [Test]
    public async Task Release_Without_A_Prior_Wait_Publishes_A_Permit()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        semaphore.Release();
        semaphore.Release();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(2);

        await semaphore.WaitAsync();
        semaphore.Wait();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task WaitAsync_Completes_When_Another_Party_Releases()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        var pending = semaphore.WaitAsync().AsTask();

        await Task.Delay(50);
        await Assert.That(pending.IsCompleted).IsFalse();

        semaphore.Release();

        await WhenAllWithTimeout([pending]);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Wait_Blocks_Until_Another_Party_Releases()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        var blocked = BlockingThread.Start(() => semaphore.Wait());

        await blocked.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);
        await Assert.That(blocked.Completion.IsCompleted).IsFalse();

        semaphore.Release();

        await WhenAllWithTimeout([blocked.Completion]);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task TryWait_Drains_Surplus_Signals()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(3);

        var drained = 0;

        while (semaphore.TryWait())
        {
            drained++;
        }

        await Assert.That(drained).IsEqualTo(3);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Queued_Waiters_Are_Woken_In_Fifo_Order()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        var completionOrder = new List<int>();
        var waiters = new List<Task>();

        // WaitAsync enqueues synchronously before returning, so call order == queue order
        for (var i = 0; i < 10; i++)
        {
            waiters.Add(Consume(semaphore.WaitAsync(), i));
        }

        for (var i = 0; i < 10; i++)
        {
            semaphore.Release();

            // One release wakes exactly one waiter
            await WhenAllWithTimeout([waiters[i]]);
            await Assert.That(waiters.Count(x => x.IsCompleted)).IsEqualTo(i + 1);
        }

        await Assert.That(completionOrder).IsEquivalentTo(Enumerable.Range(0, 10).ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);

        async Task Consume(ValueTask pending, int index)
        {
            await pending;

            lock (completionOrder)
            {
                completionOrder.Add(index);
            }
        }
    }

    [Test]
    public async Task Cancelled_Waiter_Does_Not_Swallow_A_Later_Release()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);
        using var cts = new CancellationTokenSource();

        var pending = semaphore.WaitAsync(cts.Token).AsTask();
        cts.Cancel();

        await Assert.That(async () => await pending).ThrowsExactly<OperationCanceledException>();

        // The cancelled node is still queued; this release must settle it and keep the permit
        semaphore.Release();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Timeouts_Throw_TimeoutException_And_Keep_The_Count()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.FromMilliseconds(50)))
            .ThrowsExactly<TimeoutException>();
        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.Zero))
            .ThrowsExactly<TimeoutException>();
        await Assert.That(() => semaphore.Wait(TimeSpan.FromMilliseconds(50)))
            .ThrowsExactly<TimeoutException>();
        await Assert.That(() => semaphore.Wait(TimeSpan.Zero))
            .ThrowsExactly<TimeoutException>();

        semaphore.Release();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Timed_Waits_Complete_When_A_Release_Arrives_In_Time()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        var asyncWaiter = semaphore.WaitAsync(TimeSpan.FromMinutes(5)).AsTask();
        var blockingWaiter = BlockingThread.Start(() => semaphore.Wait(TimeSpan.FromMinutes(5)));

        await blockingWaiter.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 2);

        semaphore.Release();
        semaphore.Release();

        await WhenAllWithTimeout([asyncWaiter, blockingWaiter.Completion]);

        await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(0);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Blocking_Cancellation_Throws_OperationCanceledException()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.That(() => semaphore.Wait(cts.Token)).ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task Release_At_The_Maximum_Count_Throws_Instead_Of_Wrapping()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(int.MaxValue);

        await Assert.That(() => semaphore.Release()).ThrowsExactly<SemaphoreFullException>();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task Waits_Throw_After_Dispose()
    {
        var semaphore = new Semaphores.UnpairedAsyncSemaphore(1);
        semaphore.Dispose();

        await Assert.That(async () => await semaphore.WaitAsync()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => semaphore.Wait()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => semaphore.TryWait()).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task Uncontended_Wait_And_Release_Allocate_Nothing()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(1);

        // Guards the point of the type: no releaser, so no release-state object per acquisition
        var allocated = AllocatedBytes(semaphore);

        await Assert.That(allocated).IsEqualTo(0);

        static long AllocatedBytes(Semaphores.UnpairedAsyncSemaphore semaphore)
        {
            const int iterations = 1_000;

            Cycle(semaphore);

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < iterations; i++)
            {
                Cycle(semaphore);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        static void Cycle(Semaphores.UnpairedAsyncSemaphore semaphore)
        {
            semaphore.WaitAsync().GetAwaiter().GetResult();
            semaphore.Release();
            semaphore.Wait();
            semaphore.Release();

            if (semaphore.TryWait())
            {
                semaphore.Release();
            }
        }
    }

    [Test]
    public async Task Every_Release_Wakes_Exactly_One_Waiter_Under_Contention()
    {
        const int waitersPerFlavor = 4;
        const int releasers = 4;
        const int iterationsPerWorker = 5_000;

        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        var woken = 0;

        var asyncWaiters = Enumerable.Range(0, waitersPerFlavor).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                await semaphore.WaitAsync();
                Interlocked.Increment(ref woken);
            }
        }));

        var blockingWaiters = Enumerable.Range(0, waitersPerFlavor).Select(_ => BlockingThread.Start(() =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                semaphore.Wait();
                Interlocked.Increment(ref woken);
            }
        }).Completion);

        // Exactly as many releases as waits, from threads that never wait
        var releasing = Enumerable.Range(0, releasers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2 * waitersPerFlavor * iterationsPerWorker / releasers; i++)
            {
                semaphore.Release();
            }
        }));

        await WhenAllWithTimeout(asyncWaiters.Concat(blockingWaiters).Concat(releasing).ToArray());

        await Assert.That(woken).IsEqualTo(2 * waitersPerFlavor * iterationsPerWorker);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Release_Landing_Between_The_Fast_Path_And_The_Commit_Is_Consumed_Exactly_Once()
    {
        const int maxSignals = 50_000;

        // Every handover is a hop through the pool, which takes as long as the machine and the tests
        // running next to this one make it take. Bounded by time so a small runner does fewer, not fail
        var budget = TimeSpan.FromSeconds(5);

        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        var woken = 0;
        var stopAt = 0;
        var published = 0;

        var waiting = Task.Run(async () =>
        {
            while (true)
            {
                await semaphore.WaitAsync();

                if (Interlocked.Increment(ref woken) == Volatile.Read(ref stopAt))
                {
                    return;
                }
            }
        });

        // One signal in flight at a time, published the moment the last one is consumed, so the count
        // sits at zero as the waiter comes back around. The release then lands before its fast path
        // (taken there), after its commit (handed over through the queue), or in between, where the
        // commit decrement itself has to find the permit
        var releasing = BlockingThread.Start(() =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                var last = published == maxSignals - 1 || stopwatch.Elapsed > budget;

                if (last)
                {
                    // Before the release, so the wake-up it causes already sees it
                    Volatile.Write(ref stopAt, published + 1);
                }

                semaphore.Release();
                published++;

                var spinner = default(SpinWait);

                while (Volatile.Read(ref woken) < published && !waiting.IsCompleted)
                {
                    spinner.SpinOnce();
                }

                if (last)
                {
                    return;
                }
            }
        });

        try
        {
            await WhenAllWithTimeout([waiting, releasing.Completion]);
        }
        catch (TimeoutException)
        {
            // Tells a lost wake-up (progress stopped, a waiter still queued) from a run that is merely slow
            var before = Volatile.Read(ref woken);
            await Task.Delay(TimeSpan.FromSeconds(2));

            throw new TimeoutException(
                $"Woke {before} of {Volatile.Read(ref published)} published, then {Volatile.Read(ref woken) - before} more in 2 s; " +
                $"count {semaphore.CurrentCount}, queued {semaphore.QueuedWaiterCount}.");
        }

        await Assert.That(woken).IsEqualTo(published);
        await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(0);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Cancellation_Storm_Against_Unpaired_Releases_Loses_No_Permits()
    {
        const int workers = 8;
        const int iterationsPerWorker = 500;

        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(0);

        var acquired = 0;
        var cancelled = 0;
        var released = 0;
        var stop = 0;

        var waiting = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker);

            for (var i = 0; i < iterationsPerWorker; i++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(random.Next(1, 4)));

                try
                {
                    await semaphore.WaitAsync(cts.Token);
                    Interlocked.Increment(ref acquired);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                }
            }
        })).ToArray();

        var releasing = Task.Run(async () =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                semaphore.Release();
                Interlocked.Increment(ref released);
                await Task.Delay(1);
            }
        });

        await WhenAllWithTimeout(waiting);
        Volatile.Write(ref stop, 1);
        await WhenAllWithTimeout([releasing]);

        await Assert.That(acquired + cancelled).IsEqualTo(workers * iterationsPerWorker);

        // Drain so every dead node left in the queue is settled, then count what is left: each
        // release either woke a waiter or is still available
        semaphore.Release();
        released++;

        var available = 0;

        while (semaphore.TryWait())
        {
            available++;
        }

        await Assert.That(acquired + available).IsEqualTo(released);
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
