// These tests split acquire from release and defer awaits to race an arriving caller against a
// granted waiter, which is exactly what the usage analyzers guard against.
#pragma warning disable SEM0001, SEM0002, SEM0003, SEM0004

using TUnit.Assertions.Enums;

namespace AsyncSemaphore.UnitTests;

/// <summary>
/// The overtakable grant of a single-permit gate: a release publishes the waiter it granted the permit
/// to, and until that waiter's hop to the thread pool starts, a caller that is already running may take
/// the permit from it.
/// <para>
/// Whether an arrival beats the hop is a race, tens of nanoseconds against a thread-pool dispatch. The
/// tests that need an overtake repeat a round until one happens and check the invariants on every
/// round; the tests for what must never be published read <c>HasPublishedGrant</c> straight after the
/// release, where a published grant is still visible unless its hop ran within those nanoseconds.
/// </para>
/// </summary>
public class OvertakingTests
{
    private const int Rounds = 2_000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Set on a thread for as long as it is inside a release.</summary>
    [ThreadStatic]
    private static bool t_releasing;

    [Test]
    public async Task A_Caller_That_Arrives_Before_The_Granted_Waiter_Resumes_Takes_The_Permit()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var overtakes = 0;

        for (var round = 0; round < Rounds && overtakes < 10; round++)
        {
            var holder = await semaphore.WaitAsync();

            // AsTask registers a continuation that resumes on the thread pool.
            var waiter = semaphore.WaitAsync().AsTask();

            holder.Dispose();

            var arrival = semaphore.WaitAsync();

            if (arrival.IsCompletedSuccessfully)
            {
                overtakes++;

                // The permit is the arrival's now. The waiter is owed it and counts as waiting again.
                await Assert.That(waiter.IsCompleted).IsFalse();
                await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(1);
                await Assert.That(semaphore.CurrentCount).IsEqualTo(0);

                arrival.Result.Dispose();

                (await waiter.WaitAsync(Timeout)).Dispose();
            }
            else
            {
                // The waiter's hop ran first, so the arrival queued behind it.
                (await waiter.WaitAsync(Timeout)).Dispose();
                (await arrival.AsTask().WaitAsync(Timeout)).Dispose();
            }

            await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(0);
            await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
        }

        await Assert.That(overtakes).IsGreaterThan(0);
    }

    [Test]
    public async Task An_Overtaken_Waiter_Is_Served_Ahead_Of_The_Waiters_Queued_Behind_It()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var overtakes = 0;

        for (var round = 0; round < Rounds && overtakes < 10; round++)
        {
            var completionOrder = new List<int>();
            var holder = await semaphore.WaitAsync();

            var waiters = new List<Task>();

            for (var i = 0; i < 4; i++)
            {
                waiters.Add(Consume(semaphore.WaitAsync(), i, completionOrder));
            }

            holder.Dispose();

            var arrival = semaphore.WaitAsync();

            if (arrival.IsCompletedSuccessfully)
            {
                overtakes++;

                lock (completionOrder)
                {
                    completionOrder.Add(-1);
                }

                arrival.Result.Dispose();
            }
            else
            {
                waiters.Add(Consume(arrival, 4, completionOrder));
            }

            await Task.WhenAll(waiters).WaitAsync(Timeout);

            // Overtaken or not, the queued waiters leave in the order they arrived in.
            var queued = completionOrder.Where(index => index is >= 0 and < 4).ToList();

            await Assert.That(queued).IsEquivalentTo(Enumerable.Range(0, 4).ToList(), CollectionOrdering.Matching);
            await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
        }

        await Assert.That(overtakes).IsGreaterThan(0);
    }

    [Test]
    public async Task A_Wait_Is_Overtaken_A_Bounded_Number_Of_Times()
    {
        // One thread-pool work item, with no await in it: each release then queues the waiter's hop to
        // this worker's own queue, where it stays until another worker steals it. That leaves the
        // arrival a wide margin, which a run of overtakes on one and the same wait needs.
        var mostOvertakes = await Task.Run(() =>
        {
            var most = 0;

            for (var attempt = 0; attempt < Rounds && most < Semaphores.AsyncSemaphore.MaxOvertakes; attempt++)
            {
                using var semaphore = new Semaphores.AsyncSemaphore(1);

                var holder = semaphore.Wait();
                var waiter = semaphore.WaitAsync().AsTask();
                var overtakes = 0;

                while (true)
                {
                    holder.Dispose();

                    var arrival = semaphore.WaitAsync();

                    if (!arrival.IsCompletedSuccessfully)
                    {
                        // The waiter has the permit: its hop collected the grant, or it was handed over directly.
                        if (!waiter.Wait(Timeout))
                        {
                            throw new TimeoutException("The waiter was never served.");
                        }

                        waiter.Result.Dispose();

                        var queued = arrival.AsTask();

                        if (!queued.Wait(Timeout))
                        {
                            throw new TimeoutException("The arrival was never served.");
                        }

                        queued.Result.Dispose();

                        break;
                    }

                    overtakes++;
                    holder = arrival.Result;

                    if (overtakes > Semaphores.AsyncSemaphore.MaxOvertakes)
                    {
                        holder.Dispose();

                        return overtakes;
                    }
                }

                most = Math.Max(most, overtakes);
            }

            return most;
        }).WaitAsync(TimeSpan.FromMinutes(2));

        // Reached, so the bound was really put to the test, and never passed.
        await Assert.That(mostOvertakes).IsEqualTo(Semaphores.AsyncSemaphore.MaxOvertakes);
    }

    [Test]
    public async Task A_Blocking_Wait_Overtakes_Too()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var overtakes = 0;

        for (var round = 0; round < Rounds && overtakes < 10; round++)
        {
            var completionOrder = new List<int>();
            var holder = await semaphore.WaitAsync();
            var waiter = Consume(semaphore.WaitAsync(), 0, completionOrder);

            holder.Dispose();

            // Returns at once when it overtakes, and otherwise as soon as the waiter has come and gone.
            var arrival = semaphore.Wait(Timeout);

            int served;

            lock (completionOrder)
            {
                served = completionOrder.Count;
            }

            if (served == 0)
            {
                overtakes++;

                await Assert.That(waiter.IsCompleted).IsFalse();
            }

            arrival.Dispose();

            await waiter.WaitAsync(Timeout);
            await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
        }

        await Assert.That(overtakes).IsGreaterThan(0);
    }

    [Test]
    public async Task A_Waiter_With_No_Continuation_Completes_On_The_Release()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        for (var round = 0; round < Rounds; round++)
        {
            var holder = await semaphore.WaitAsync();
            var pending = semaphore.WaitAsync();

            holder.Dispose();

            var published = semaphore.HasPublishedGrant;
            var completed = pending.IsCompletedSuccessfully;

            // Nothing is registered to hop for, so the wait is complete as soon as the release returns.
            await Assert.That(published).IsFalse();
            await Assert.That(completed).IsTrue();

            pending.Result.Dispose();
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_Waiter_That_Can_Still_Be_Cancelled_Is_Handed_The_Permit_Directly()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        using var cancellation = new CancellationTokenSource();

        for (var round = 0; round < Rounds; round++)
        {
            var holder = await semaphore.WaitAsync();

            var waiter = (round % 2) == 0
                ? semaphore.WaitAsync(cancellation.Token).AsTask()
                : semaphore.WaitAsync(TimeSpan.FromMinutes(5)).AsTask();

            holder.Dispose();

            var published = semaphore.HasPublishedGrant;

            // Claiming the waiter switched its token and timeout off. Overtaken, it would sit out the
            // overtaker's critical section with no way to give up.
            await Assert.That(published).IsFalse();

            (await waiter.WaitAsync(Timeout)).Dispose();
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_Blocking_Waiter_Is_Handed_The_Permit_Directly()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        for (var round = 0; round < 20; round++)
        {
            var holder = await semaphore.WaitAsync();

            var blocked = BlockingThread.Start(() => semaphore.Wait().Dispose());

            await blocked.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);

            holder.Dispose();

            var published = semaphore.HasPublishedGrant;

            await Assert.That(published).IsFalse();

            await blocked.Completion.WaitAsync(Timeout);
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_Gate_With_More_Than_One_Permit_Never_Publishes_A_Grant()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(2);

        for (var round = 0; round < Rounds; round++)
        {
            var first = await semaphore.WaitAsync();
            var second = await semaphore.WaitAsync();
            var waiter = semaphore.WaitAsync().AsTask();

            first.Dispose();

            var published = semaphore.HasPublishedGrant;

            // With two holders, two releases can run at once, and the overtaken slot has room for one.
            await Assert.That(published).IsFalse();

            second.Dispose();

            (await waiter.WaitAsync(Timeout)).Dispose();
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(2);
    }

    [Test]
    public async Task An_Unpaired_Semaphore_Never_Publishes_A_Grant()
    {
        using var semaphore = new Semaphores.UnpairedAsyncSemaphore(1);

        for (var round = 0; round < Rounds; round++)
        {
            await semaphore.WaitAsync();

            var waiter = semaphore.WaitAsync().AsTask();

            semaphore.Release();

            var published = semaphore.HasPublishedGrant;

            // Anyone may release it, at any time, so no caller is ever the only one who can.
            await Assert.That(published).IsFalse();

            await waiter.WaitAsync(Timeout);

            semaphore.Release();
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_Continuation_Bound_For_A_Captured_Context_Is_Not_Sent_Through_The_Thread_Pool_First()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        for (var round = 0; round < 200; round++)
        {
            var holder = await semaphore.WaitAsync();
            var context = new PostCountingContext();
            var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Registers the way an await without ConfigureAwait(false) does under a context.
            var registration = BlockingThread.Start(() =>
            {
                SynchronizationContext.SetSynchronizationContext(context);

                var awaiter = semaphore.WaitAsync().GetAwaiter();

                awaiter.OnCompleted(() =>
                {
                    try
                    {
                        awaiter.GetResult().Dispose();
                        resumed.SetResult(true);
                    }
                    catch (Exception exception)
                    {
                        resumed.SetException(exception);
                    }
                });
            });

            await registration.Completion.WaitAsync(Timeout);

            holder.Dispose();

            var published = semaphore.HasPublishedGrant;

            await Assert.That(published).IsFalse();

            await resumed.Task.WaitAsync(Timeout);

            await Assert.That(context.Posts).IsEqualTo(1);
            await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task A_Node_Resumed_By_Its_Hop_Goes_Back_To_Resuming_Asynchronously()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        using var cancellation = new CancellationTokenSource();

        var resumedInsideARelease = 0;

        for (var round = 0; round < Rounds; round++)
        {
            // A plain waiter is resumed by its hop, which completes the node inline on the pool thread.
            // Awaiting it carries this method onto that thread, where the node now sits in the cache.
            await ReleaseTo(() => semaphore.WaitAsync());

            // So this wait rents the same node. It can be cancelled, which means a direct handoff, and
            // that must not run the continuation inside the release.
            await ReleaseTo(() => semaphore.WaitAsync(cancellation.Token));
        }

        await Assert.That(resumedInsideARelease).IsEqualTo(0);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        async Task ReleaseTo(Func<ValueTask<Semaphores.AsyncSemaphoreReleaser>> wait)
        {
            var holder = await semaphore.WaitAsync();
            var waiter = Observe(wait());

            t_releasing = true;
            holder.Dispose();
            t_releasing = false;

            await waiter.WaitAsync(Timeout);
        }

        async Task Observe(ValueTask<Semaphores.AsyncSemaphoreReleaser> pending)
        {
            using var @lock = await pending.ConfigureAwait(false);

            if (t_releasing)
            {
                Interlocked.Increment(ref resumedInsideARelease);
            }
        }
    }

    [Test]
    public async Task Blocked_Callers_Of_An_Async_Method_Keep_Mutual_Exclusion_While_Grants_Are_Overtaken()
    {
        // The workload of issue #589: every caller blocks on an async method that takes the gate, works
        // outside it, and takes it again. Resumed waiters arrive at the second acquisition while other
        // grants are in flight, so overtaking runs flat out against queueing, hops and direct handoffs.
        var workers = Math.Max(4, Environment.ProcessorCount);

        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var inCriticalSection = 0;
        var violations = 0;
        long entries = 0;

        await Task.Run(() =>
        {
            for (var round = 0; round < 500; round++)
            {
                Parallel.For(0, workers, _ => TakeWorkTake().AsTask().GetAwaiter().GetResult());
            }
        }).WaitAsync(TimeSpan.FromMinutes(2));

        await Assert.That(violations).IsEqualTo(0);
        await Assert.That(entries).IsEqualTo(500L * workers * 2);
        await Assert.That(semaphore.QueuedWaiterCount).IsEqualTo(0);
        await Assert.That(semaphore.HasPublishedGrant).IsFalse();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        async ValueTask TakeWorkTake()
        {
            for (var i = 0; i < 2; i++)
            {
                using (await semaphore.WaitAsync().ConfigureAwait(false))
                {
                    if (Interlocked.Increment(ref inCriticalSection) != 1)
                    {
                        Interlocked.Increment(ref violations);
                    }

                    entries++;

                    Interlocked.Decrement(ref inCriticalSection);
                }

                Thread.SpinWait(50);
            }
        }
    }

    private static async Task Consume(ValueTask<Semaphores.AsyncSemaphoreReleaser> pending, int index, List<int> completionOrder)
    {
        using var @lock = await pending.ConfigureAwait(false);

        lock (completionOrder)
        {
            completionOrder.Add(index);
        }
    }

    /// <summary>Runs what it is posted on the thread pool, and counts the posts.</summary>
    private sealed class PostCountingContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);

            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }
}
