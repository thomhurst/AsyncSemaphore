// These tests defer awaits and split acquire from release to race a wait's timer, its token
// registration and its claim against each other, which is exactly what the usage analyzers guard against.
#pragma warning disable SEM0001, SEM0002, SEM0003, SEM0004

namespace AsyncSemaphore.UnitTests;

/// <summary>
/// A wait armed with both a live token and a finite timeout, a cancellation that lands while the
/// wait is still arming, and a timeout callback that is already running when its node is claimed.
/// </summary>
public class CancellationAndTimeoutTests
{
    private static readonly TimeSpan StressTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(5);

    [Test]
    public async Task Token_Cancelled_Before_The_Timeout_Throws_OperationCanceledException()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        using var cts = new CancellationTokenSource();

        var holder = await semaphore.WaitAsync();

        // Both armed: the cancellation has to win the state CAS and then disarm the timer
        var pending = semaphore.WaitAsync(LongTimeout, cts.Token).AsTask();

        await Assert.That(pending.IsCompleted).IsFalse();

        cts.Cancel();

        var thrown = await Assert.That(async () => await pending).ThrowsExactly<OperationCanceledException>();
        await Assert.That(thrown!.CancellationToken).IsEqualTo(cts.Token);

        // The dead node is still queued; the release must settle it and keep the permit
        holder.Dispose();
        await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(0);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Timeout_Elapsing_Before_The_Token_Throws_TimeoutException()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        using var cts = new CancellationTokenSource();

        var holder = await semaphore.WaitAsync();

        // Both armed: the timeout has to win the state CAS and then drop the registration
        var pending = semaphore.WaitAsync(TimeSpan.FromMilliseconds(50), cts.Token).AsTask();

        await Assert.That(await Task.WhenAny(pending, Task.Delay(StressTimeout))).IsSameReferenceAs(pending);
        await Assert.That(async () => await pending).ThrowsExactly<TimeoutException>();

        // A cancellation after the timeout finds nothing registered, and must not settle the node twice
        cts.Cancel();

        holder.Dispose();
        await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(0);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Acquisition_Disarms_Both_The_Token_And_The_Timeout()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        using var cts = new CancellationTokenSource();

        var holder = await semaphore.WaitAsync();
        var pending = semaphore.WaitAsync(TimeSpan.FromMilliseconds(200), cts.Token);

        // Released inline: a release queued to a starved pool can land after the timer has fired
        holder.Dispose();

        using (await pending)
        {
            // Neither may revoke a permit that has been handed over
            cts.Cancel();
            await Task.Delay(400);

            await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        using var final = await semaphore.WaitAsync();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Token_And_Timeout_Storm_Accounts_For_Every_Wait()
    {
        const int maxCount = 2;
        const int workers = 8;
        const int iterationsPerWorker = 200;

        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);

        var holders = 0;
        var maxObserved = 0;
        var acquired = 0;
        var cancelled = 0;
        var timedOut = 0;

        var tasks = Enumerable.Range(0, workers).Select(workerIndex => Task.Run(async () =>
        {
            var random = new Random(workerIndex * 15485863);

            for (var i = 0; i < iterationsPerWorker; i++)
            {
                using var cts = new CancellationTokenSource();

                // Every wait arms a timer and a registration, so all three of claim, timeout and
                // cancellation race for the same node
                var pending = semaphore.WaitAsync(TimeSpan.FromMilliseconds(random.Next(1, 8)), cts.Token);
                var cancelTask = random.Next(3) == 0 ? Task.Run(cts.Cancel) : Task.CompletedTask;

                try
                {
                    using (await pending)
                    {
                        var current = Interlocked.Increment(ref holders);
                        InterlockedMax(ref maxObserved, current);

                        // Hold long enough, some of the time, for the waiters behind to time out
                        if (random.Next(4) == 0)
                        {
                            await Task.Delay(1);
                        }
                        else
                        {
                            await Task.Yield();
                        }

                        Interlocked.Decrement(ref holders);
                        Interlocked.Increment(ref acquired);
                    }
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                }
                catch (TimeoutException)
                {
                    Interlocked.Increment(ref timedOut);
                }

                await cancelTask;
            }
        })).ToArray();

        await WhenAllWithTimeout(tasks);

        await Assert.That(acquired + cancelled + timedOut).IsEqualTo(workers * iterationsPerWorker);
        await Assert.That(maxObserved).IsLessThanOrEqualTo(maxCount);

        // Dead nodes may still be queued; taking and returning every permit settles them
        await AssertAllPermitsAvailable(semaphore, maxCount);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_Landing_While_The_Wait_Is_Still_Arming_Loses_No_Permits(bool withTimeout)
    {
        const int iterations = 20_000;
        const int delaySweep = 32;

        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var rejectedUpFront = 0;
        var cancelledOnceArmed = 0;

        // Both sides run on threads of their own and nothing in the loop awaits: the window is a few
        // instructions wide, and a hop through the pool on either side would miss it by microseconds
        var racing = BlockingThread.Start(() =>
        {
            using var canceller = new RacingThread();

            for (var i = 0; i < iterations; i++)
            {
                var holder = semaphore.Wait();

                using var cts = new CancellationTokenSource();

                // Sweep the cancel across the call from both directions: later and later into it, then
                // earlier and earlier ahead of it. It lands before the up-front check, between that check
                // and the registration, and (with a timeout) between the registration and the timer
                var offset = i % (2 * delaySweep);
                var cancelDelaySpins = offset < delaySweep ? offset : 0;
                var waitDelaySpins = offset < delaySweep ? 0 : offset - delaySweep;

                canceller.Fire(cts.Cancel, cancelDelaySpins);

                if (waitDelaySpins > 0)
                {
                    Thread.SpinWait(waitDelaySpins);
                }

                ValueTask<Semaphores.AsyncSemaphoreReleaser> pending = default;
                var rejected = false;

                try
                {
                    pending = withTimeout
                        ? semaphore.WaitAsync(LongTimeout, cts.Token)
                        : semaphore.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    rejected = true;
                }

                canceller.Join();

                if (rejected)
                {
                    rejectedUpFront++;
                }
                else
                {
                    // Cancel has returned and the permit is still held, so the wait is over already: the
                    // callback completes it inline, whether the canceller or the registration ran it
                    Require(pending.IsCompleted, "the wait is still pending after Cancel returned");

                    try
                    {
                        pending.GetAwaiter().GetResult().Dispose();

                        Require(false, "the wait acquired a permit that was never released");
                    }
                    catch (OperationCanceledException)
                    {
                        cancelledOnceArmed++;
                    }
                }

                // A wait that was rejected up front never queued; one that armed is queued dead. Either
                // way this single release must leave exactly one permit and no debt
                holder.Dispose();

                Require(semaphore.QueuedWaiterCount == 0, $"{semaphore.QueuedWaiterCount} waiters of debt left after the release");
                Require(semaphore.CurrentCount == 1, $"count is {semaphore.CurrentCount} after the release");
            }
        });

        await WhenAllWithTimeout([racing.Completion]);

        await Assert.That(rejectedUpFront + cancelledOnceArmed).IsEqualTo(iterations);

        static void Require(bool condition, string failure)
        {
            if (!condition)
            {
                throw new InvalidOperationException(failure);
            }
        }
    }

    [Test]
    public async Task Timeout_Callback_In_Flight_At_The_Claim_Cannot_Poison_A_Recycled_Node()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();

        var gate = new TimerCallbackGate();
        var pending = gate.Arm(() => semaphore.WaitAsync(TimeSpan.FromMilliseconds(20)));

        // The timer has fired and counts its callback as running, but OnTimeout has not started
        await WhenAllWithTimeout([gate.Entered]);

        // The claim wins the state CAS, and cannot prove that the timer is quiescent
        holder.Dispose();

        var acquired = await pending;

        // Still on the thread that ran GetResult, with no await in between: these rent from the
        // thread-local cache, the instance slot and the shared pool, which is everywhere a node that
        // was wrongly recycled could have gone
        var queued = new ValueTask<Semaphores.AsyncSemaphoreReleaser>[4];

        for (var i = 0; i < queued.Length; i++)
        {
            queued[i] = semaphore.WaitAsync();
        }

        // Only now does the stale OnTimeout run. Its CAS must find the claimed node, not a recycled one
        gate.Proceed();
        await WhenAllWithTimeout([gate.Exited]);

        acquired.Dispose();

        foreach (var wait in queued)
        {
            // A poisoned node would fault here with the TimeoutException of a wait that never had a timeout
            using var @lock = await wait;
        }

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

        await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(0);
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
            throw new TimeoutException($"Test did not finish within {StressTimeout}; a waiter or a timer callback was likely lost.");
        }

        await all;
    }

    /// <summary>
    /// Parks a timer callback after the timer has counted it as running and before its body starts,
    /// without a seam in the library. A <see cref="Timer"/> captures the ExecutionContext it is created
    /// under and restores it on the thread that fires it, and an <see cref="AsyncLocal{T}"/> reports
    /// that restore synchronously on that thread. So a value set only around the wait call that arms
    /// the timer is seen again exactly once: on the timer's thread, just ahead of OnTimeout.
    /// </summary>
    private sealed class TimerCallbackGate
    {
        private static readonly TimeSpan ParkTimeout = TimeSpan.FromSeconds(30);
        private static readonly AsyncLocal<TimerCallbackGate?> Current = new(OnContextChanged);

        private readonly ManualResetEventSlim _proceed = new();
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the timer callback is parked.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Completes once the timer callback has returned.</summary>
        public Task Exited => _exited.Task;

        /// <summary>Runs <paramref name="wait"/> so that the timer it arms (and nothing else) carries this gate.</summary>
        public ValueTask<Semaphores.AsyncSemaphoreReleaser> Arm(Func<ValueTask<Semaphores.AsyncSemaphoreReleaser>> wait)
        {
            Current.Value = this;

            try
            {
                return wait();
            }
            finally
            {
                Current.Value = null;
            }
        }

        public void Proceed()
        {
            _proceed.Set();
        }

        // An exception escaping a change notification is fatal to the process, so nothing here may throw
        private static void OnContextChanged(AsyncLocalValueChangedArgs<TimerCallbackGate?> args)
        {
            // The sets in Arm are not a context switch; only the timer's thread restoring the context is
            if (!args.ThreadContextChanged)
            {
                return;
            }

            if (args.CurrentValue is { } entering)
            {
                entering._entered.TrySetResult(true);
                entering._proceed.Wait(ParkTimeout);
            }
            else if (args.PreviousValue is { } leaving)
            {
                leaving._exited.TrySetResult(true);
            }
        }
    }
}
