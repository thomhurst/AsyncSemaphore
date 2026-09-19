// These tests keep handles outside of a using and abandon waits that are expected to throw,
// which is exactly what the usage analyzers guard against.
#pragma warning disable SEM0001, SEM0002, SEM0003, SEM0004

namespace AsyncSemaphore.UnitTests;

/// <summary>Argument validation, the accepted timeout range, and what disposal does and does not change.</summary>
public class ApiContractTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task Constructor_Rejects_A_Count_Below_One(int maxCount)
    {
        await Assert.That(() => new Semaphores.AsyncSemaphore(maxCount))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Every_WaitAsync_Overload_Throws_After_Dispose()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);
        semaphore.Dispose();

        await Assert.That(async () => await semaphore.WaitAsync())
            .ThrowsExactly<ObjectDisposedException>();
        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.FromSeconds(1)))
            .ThrowsExactly<ObjectDisposedException>();
        await Assert.That(async () => await semaphore.WaitAsync(CancellationToken.None))
            .ThrowsExactly<ObjectDisposedException>();
        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None))
            .ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => semaphore.Wait(TimeSpan.FromSeconds(1)))
            .ThrowsExactly<ObjectDisposedException>();

        // The rejected waits never touched the count
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Releaser_Disposed_After_The_Semaphore_Still_Returns_Its_Permit()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();
        semaphore.Dispose();

        // A using block that outlives the semaphore must not throw on the way out
        holder.Dispose();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Waiters_Queued_Before_Dispose_Are_Still_Served_In_Order()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();
        var completionOrder = new List<string>();

        var asyncWaiter = Consume(semaphore.WaitAsync(), "async");

        var blockingWaiter = BlockingThread.Start(() =>
        {
            using var @lock = semaphore.Wait();
            Record("blocking");
        });

        await blockingWaiter.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 2);

        // Dispose only turns new waits away; it neither fails nor strands the waits already queued
        semaphore.Dispose();
        holder.Dispose();

        await Task.WhenAll(asyncWaiter, blockingWaiter.Completion).WaitAsync(TestTimeout);

        await Assert.That(completionOrder).IsEquivalentTo(["async", "blocking"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
    [MethodDataSource(nameof(BoundaryTimeouts))]
    public async Task Timeout_Range_Is_Minus_One_To_Int32_MaxValue_Milliseconds_For_Every_Wait_Flavor(long timeoutTicks, bool accepted)
    {
        var timeout = TimeSpan.FromTicks(timeoutTicks);
        var expectedToThrow = !accepted;

        // Every semaphore below has a permit available, so an accepted timeout acquires at once
        // and only the range check can throw
        await Assert.That(Rejects(() =>
        {
            using var semaphore = new Semaphores.AsyncSemaphore(1);
            semaphore.WaitAsync(timeout).GetAwaiter().GetResult().Dispose();
        })).IsEqualTo(expectedToThrow);

        await Assert.That(Rejects(() =>
        {
            using var semaphore = new Semaphores.AsyncSemaphore(1);
            semaphore.WaitAsync(timeout, CancellationToken.None).GetAwaiter().GetResult().Dispose();
        })).IsEqualTo(expectedToThrow);

        await Assert.That(Rejects(() =>
        {
            using var semaphore = new Semaphores.AsyncSemaphore(1);
            semaphore.Wait(timeout).Dispose();
        })).IsEqualTo(expectedToThrow);

        await Assert.That(Rejects(() =>
        {
            using var semaphore = new Semaphores.UnpairedAsyncSemaphore(1);
            semaphore.WaitAsync(timeout).GetAwaiter().GetResult();
        })).IsEqualTo(expectedToThrow);

        await Assert.That(Rejects(() =>
        {
            using var semaphore = new Semaphores.UnpairedAsyncSemaphore(1);
            semaphore.Wait(timeout);
        })).IsEqualTo(expectedToThrow);

        static bool Rejects(Action wait)
        {
            try
            {
                wait();

                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                return true;
            }
        }
    }

    [Test]
    public async Task A_Rejected_Timeout_Leaves_The_Count_Untouched()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.FromMilliseconds(-2)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.FromMilliseconds(int.MaxValue + 1L), CancellationToken.None))
            .ThrowsExactly<ArgumentOutOfRangeException>();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Infinite_Timeout_Waits_For_The_Permit_Instead_Of_Timing_Out()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var holder = await semaphore.WaitAsync();

        var asyncWaiter = semaphore.WaitAsync(Timeout.InfiniteTimeSpan).AsTask();

        var blockingWaiter = BlockingThread.Start(() =>
        {
            using var @lock = semaphore.Wait(Timeout.InfiniteTimeSpan);
        });

        await blockingWaiter.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 2);
        await Task.Delay(100);

        await Assert.That(asyncWaiter.IsCompleted).IsFalse();
        await Assert.That(blockingWaiter.Completion.IsCompleted).IsFalse();

        holder.Dispose();

        (await asyncWaiter.WaitAsync(TestTimeout)).Dispose();
        await blockingWaiter.Completion.WaitAsync(TestTimeout);

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_Timeout_Below_One_Millisecond_Times_Out_Instead_Of_Waiting_Forever()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        // Truncates to zero milliseconds without being TimeSpan.Zero, so it takes the queued path
        var oneTick = TimeSpan.FromTicks(1);

        using (await semaphore.WaitAsync())
        {
            var pending = semaphore.WaitAsync(oneTick).AsTask();

            // Task.WaitAsync would report a hang as a TimeoutException too, so race a delay instead
            await Assert.That(await Task.WhenAny(pending, Task.Delay(TestTimeout))).IsSameReferenceAs(pending);
            await Assert.That(async () => await pending).ThrowsExactly<TimeoutException>();

            await Assert.That(() => semaphore.Wait(oneTick))
                .ThrowsExactly<TimeoutException>();
        }

        // Both timed-out nodes were still queued; the release above must have settled them
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    /// <summary>
    /// Ticks and whether they are in range: the timeout, truncated to whole milliseconds, has to be -1
    /// (infinite) or lie in [0, int.MaxValue]. SemaphoreSlim set that range; from .NET 10 it no longer
    /// enforces the upper end, so the expectations are spelled out here instead of read off it.
    /// </summary>
    public static IEnumerable<(long TimeoutTicks, bool Accepted)> BoundaryTimeouts()
    {
        const long maxMilliseconds = int.MaxValue;

        return
        [
            (TimeSpan.MinValue.Ticks, false),
            (-2 * TimeSpan.TicksPerMillisecond, false),

            // Truncates to -1 ms, which reads as infinite
            ((-2 * TimeSpan.TicksPerMillisecond) + 1, true),
            (-TimeSpan.TicksPerMillisecond, true),
            (-1, true),
            (0, true),
            (1, true),
            (TimeSpan.TicksPerMillisecond, true),
            (maxMilliseconds * TimeSpan.TicksPerMillisecond, true),

            // Still truncates to int.MaxValue ms
            (((maxMilliseconds + 1) * TimeSpan.TicksPerMillisecond) - 1, true),
            ((maxMilliseconds + 1) * TimeSpan.TicksPerMillisecond, false),
            (TimeSpan.MaxValue.Ticks, false),
        ];
    }
}
