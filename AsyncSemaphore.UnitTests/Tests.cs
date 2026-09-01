namespace AsyncSemaphore.UnitTests;

public class Tests
{
    [Test]
    public async Task Can_Enter_Immediately()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);
        
        var time = await Measure(async () =>
        {
            using var @lock = await semaphore.WaitAsync();
        });
        
        await Assert.That(time).IsLessThan(TimeSpan.FromMilliseconds(100));
    }
    
    [Test]
    [MethodDataSource(nameof(LoopCounts))]
    public async Task WaitsForPreviousSemaphore(int loopCount)
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);
        
        var time = await Measure(async () =>
        {
            for (var i = 0; i < loopCount; i++)
            {
                using var @lock = await semaphore.WaitAsync();
                await DoSomething();
            }
        });

        await Assert.That(time).IsGreaterThan(TimeSpan.FromMilliseconds(500 * (loopCount - 1)));
    }
    
    [Test]
    [MethodDataSource(nameof(LoopCounts))]
    public async Task WaitsForPreviousSemaphore_Even_When_Exception_Thrown(int loopCount)
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);
        
        var time = await Measure(async () =>
        {
            for (var i = 0; i < loopCount; i++)
            {
                try
                {
                    using var @lock = await semaphore.WaitAsync();
                    await DoSomething();
                    throw new Exception();
                }
                catch
                {
                    // ignored
                }
            }
        });

        await Assert.That(time).IsGreaterThan(TimeSpan.FromMilliseconds(500 * (loopCount - 1)));
    }

    [Test]
    public async Task Timeout_Throws_TimeoutException()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);

        // Acquire the only slot
        using var @lock = await semaphore.WaitAsync();

        // A second wait with a short timeout should throw
        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.FromMilliseconds(50)))
            .ThrowsExactly<TimeoutException>();
    }

    [Test]
    public async Task Timeout_Does_Not_Corrupt_Semaphore_Count()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);

        // Acquire the only slot
        using (await semaphore.WaitAsync())
        {
            // Timeout while held
            await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.FromMilliseconds(50)))
                .ThrowsExactly<TimeoutException>();
        }

        // Semaphore should be released and available again
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        // Should be able to acquire again immediately
        using var @lock = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Timeout_With_CancellationToken_Throws_TimeoutException()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);

        using var @lock = await semaphore.WaitAsync();

        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .ThrowsExactly<TimeoutException>();
    }

    [Test]
    public async Task Cancellation_Throws_OperationCanceledException()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);

        using var @lock = await semaphore.WaitAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.That(async () => await semaphore.WaitAsync(cts.Token))
            .ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task Mutual_Exclusion_Is_Never_Violated_Under_Contention()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var inCriticalSection = 0;
        var maxObserved = 0;
        var completedIterations = 0;

        const int workers = 8;
        const int iterationsPerWorker = 5_000;

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                using var @lock = await semaphore.WaitAsync();

                var current = Interlocked.Increment(ref inCriticalSection);
                InterlockedMax(ref maxObserved, current);
                Interlocked.Decrement(ref inCriticalSection);
                Interlocked.Increment(ref completedIterations);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        await Assert.That(maxObserved).IsEqualTo(1);
        await Assert.That(completedIterations).IsEqualTo(workers * iterationsPerWorker);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Permit_Limit_Is_Never_Exceeded_Under_Contention()
    {
        const int maxCount = 4;
        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);

        var holders = 0;
        var maxObserved = 0;

        const int workers = 16;
        const int iterationsPerWorker = 2_000;

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                using var @lock = await semaphore.WaitAsync();

                var current = Interlocked.Increment(ref holders);
                InterlockedMax(ref maxObserved, current);
                await Task.Yield();
                Interlocked.Decrement(ref holders);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        await Assert.That(maxObserved).IsLessThanOrEqualTo(maxCount);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(maxCount);
    }

    [Test]
    public async Task Cancellation_Storm_Does_Not_Corrupt_Count()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        const int iterations = 500;

        for (var i = 0; i < iterations; i++)
        {
            using var holder = await semaphore.WaitAsync();

            using var cts = new CancellationTokenSource();

            var waiterTask = semaphore.WaitAsync(cts.Token);

            cts.Cancel();

            try
            {
                using var _ = await waiterTask;
            }
            catch (OperationCanceledException)
            {
                // expected when cancellation won the race
            }
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        using var final = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_Cancel_And_Release_Race_Does_Not_Corrupt_Count()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        const int iterations = 2_000;

        for (var i = 0; i < iterations; i++)
        {
            var holder = await semaphore.WaitAsync();

            using var cts = new CancellationTokenSource();

            var waiterTask = semaphore.WaitAsync(cts.Token);

            // Fire the release and the cancellation at the same time so the
            // claim/cancel CAS race is exercised in both directions
            var releaseTask = Task.Run(holder.Dispose);
            var cancelTask = Task.Run(cts.Cancel);

            await Task.WhenAll(releaseTask, cancelTask);

            try
            {
                using var _ = await waiterTask;
            }
            catch (OperationCanceledException)
            {
                // cancellation won the race; the release settles the dead node
            }
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        using var final = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Timed_Out_Waiters_Do_Not_Block_Later_Waiters()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        using (await semaphore.WaitAsync())
        {
            // Queue up several waiters that all time out while the permit is held
            var timedOutWaiters = Enumerable.Range(0, 5)
                .Select(async _ =>
                {
                    try
                    {
                        using var held = await semaphore.WaitAsync(TimeSpan.FromMilliseconds(50));
                        return false;
                    }
                    catch (TimeoutException)
                    {
                        return true;
                    }
                })
                .ToArray();

            var results = await Task.WhenAll(timedOutWaiters);
            await Assert.That(results.All(timedOut => timedOut)).IsTrue();
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        using var @lock = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Zero_Timeout_Throws_Immediately_When_Held()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        using var @lock = await semaphore.WaitAsync();

        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.Zero))
            .ThrowsExactly<TimeoutException>();
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

    private Task DoSomething()
    {
        return Task.Delay(500);
    }

    public static IEnumerable<int> LoopCounts() => Enumerable.Range(1, 10);

    private async Task<TimeSpan> Measure(Func<Task> func)
    {
        var start = DateTime.Now;

        await func();

        return DateTime.Now - start;
    } 
}
