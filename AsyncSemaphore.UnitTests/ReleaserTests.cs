// These tests deliberately copy and dispose handles to verify the ownership contract.
#pragma warning disable SEM0001, SEM0002, SEM0003, SEM0004

namespace AsyncSemaphore.UnitTests;

public class ReleaserTests
{
    [Test]
    public async Task Copied_Using_Handles_Release_Once()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        await Acquire();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        async Task Acquire()
        {
            using var first = await semaphore.WaitAsync();
            using var second = first;
        }
    }

    [Test]
    public async Task Concurrent_Boxed_Copies_Release_Once()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        var handle = await semaphore.WaitAsync();
        var copies = Enumerable.Range(0, 32).Select(_ => (IDisposable)handle).ToArray();
        await Task.WhenAll(copies.Select(copy => Task.Run(copy.Dispose))).WaitAsync(TimeSpan.FromSeconds(10));
        handle.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Stale_Copy_Cannot_Release_A_Later_Acquisition()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        var original = await semaphore.WaitAsync();
        var stale = original;
        var pending = semaphore.WaitAsync();
        original.Dispose();

        using var next = await pending;
        stale.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.Zero))
            .ThrowsExactly<TimeoutException>();
    }

    [Test]
    public async Task Queued_Acquisition_Copies_Release_Once()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        var holder = await semaphore.WaitAsync();
        var pending = semaphore.WaitAsync();
        holder.Dispose();
        var acquired = await pending;
        var copy = acquired;
        acquired.Dispose();
        copy.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    // A gate with one permit shares its release decision through the gate's own epoch while it is
    // at rest, any other through a state object per acquisition, so the contract is checked on both.
    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task Stale_Copy_Cannot_Release_A_Later_Uncontended_Acquisition(int maxCount)
    {
        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);
        var others = new Semaphores.AsyncSemaphoreReleaser[maxCount - 1];

        for (var i = 0; i < others.Length; i++)
        {
            others[i] = await semaphore.WaitAsync();
        }

        var original = await semaphore.WaitAsync();
        var stale = original;
        original.Dispose();

        // Nothing is queued, so this one comes off the fast path instead of a handoff
        using var next = await semaphore.WaitAsync();
        stale.Dispose();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        await Assert.That(semaphore.TryWait(out _)).IsFalse();

        foreach (var other in others)
        {
            other.Dispose();
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task Copies_From_Every_Earlier_Acquisition_Stay_Inert(int maxCount)
    {
        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);
        var stale = new List<IDisposable>();

        for (var i = 0; i < 1_000; i++)
        {
            if (i == 500)
            {
                // The acquisitions that follow a blocked wait release through a state of their own,
                // so the copies are a mix of both kinds
                await BlockOnce(semaphore);
            }

            var handle = await semaphore.WaitAsync();
            stale.Add(handle);
            handle.Dispose();
        }

        var holders = new Semaphores.AsyncSemaphoreReleaser[maxCount];

        for (var i = 0; i < holders.Length; i++)
        {
            holders[i] = await semaphore.WaitAsync();
        }

        foreach (var copy in stale)
        {
            copy.Dispose();
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        await Assert.That(semaphore.TryWait(out _)).IsFalse();

        foreach (var holder in holders)
        {
            holder.Dispose();
        }

        await Assert.That(semaphore.CurrentCount).IsEqualTo(maxCount);
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task Handles_From_Every_Wait_Flavor_Release_Once(int maxCount)
    {
        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);
        using var cancellation = new CancellationTokenSource();
        var timeout = TimeSpan.FromMinutes(1);

        await ReleasesOnce(await semaphore.WaitAsync());
        await ReleasesOnce(await semaphore.WaitAsync(timeout));
        await ReleasesOnce(await semaphore.WaitAsync(cancellation.Token));
        await ReleasesOnce(await semaphore.WaitAsync(timeout, cancellation.Token));
        await ReleasesOnce(semaphore.Wait());
        await ReleasesOnce(semaphore.Wait(timeout));
        await Assert.That(semaphore.TryWait(out var attempted)).IsTrue();
        await ReleasesOnce(attempted);

        async Task ReleasesOnce(Semaphores.AsyncSemaphoreReleaser handle)
        {
            var copy = handle;
            IDisposable boxed = handle;
            copy.Dispose();
            boxed.Dispose();
            handle.Dispose();
            await Assert.That(semaphore.CurrentCount).IsEqualTo(maxCount);

            // The permit is out again, and none of the spent copies can hand it back
            using var next = await semaphore.WaitAsync();
            copy.Dispose();
            boxed.Dispose();
            handle.Dispose();
            await Assert.That(semaphore.CurrentCount).IsEqualTo(maxCount - 1);
        }
    }

    [Test]
    public async Task Queued_Blocking_Acquisition_Copies_Release_Once()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        var holder = await semaphore.WaitAsync();
        var stale = holder;

        var waiter = BlockingThread.Start(() =>
        {
            var acquired = semaphore.Wait();
            var copy = acquired;
            acquired.Dispose();
            copy.Dispose();
        });

        await waiter.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);
        holder.Dispose();
        await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        stale.Dispose();

        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task Copies_Disposed_At_The_Same_Instant_Release_Once(int maxCount)
    {
        const int iterations = 50_000;
        const int delaySweep = 32;

        using var semaphore = new Semaphores.AsyncSemaphore(maxCount);

        // The release decision is a few instructions wide, so both sides run on threads of their own
        // and nothing in the loop awaits: a hop through the pool would miss it by microseconds
        var racing = BlockingThread.Start(() =>
        {
            using var other = new RacingThread();

            for (var i = 0; i < iterations; i++)
            {
                var handle = semaphore.Wait();
                IDisposable copy = handle;

                // Sweep the two disposals across each other from both directions
                var offset = i % (2 * delaySweep);
                var copyDelaySpins = offset < delaySweep ? offset : 0;
                var ownerDelaySpins = offset < delaySweep ? 0 : offset - delaySweep;

                other.Fire(copy.Dispose, copyDelaySpins);

                if (ownerDelaySpins > 0)
                {
                    Thread.SpinWait(ownerDelaySpins);
                }

                handle.Dispose();
                other.Join();

                if (semaphore.CurrentCount != maxCount)
                {
                    throw new InvalidOperationException($"count is {semaphore.CurrentCount} after round {i}: both copies released");
                }
            }
        });

        await racing.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task Handle_Of_One_Gate_Cannot_Release_Another()
    {
        using var first = new Semaphores.AsyncSemaphore(1);
        using var second = new Semaphores.AsyncSemaphore(1);

        // Both gates are at the same epoch, so only the gate reference tells the handles apart
        var firstHandle = await first.WaitAsync();
        using var secondHandle = await second.WaitAsync();
        var copy = firstHandle;
        firstHandle.Dispose();
        copy.Dispose();

        await Assert.That(first.CurrentCount).IsEqualTo(1);
        await Assert.That(second.CurrentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Handles_Release_Once_On_Both_Sides_Of_A_Blocked_Wait()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);

        var throughEpoch = await semaphore.WaitAsync();
        var staleThroughEpoch = throughEpoch;
        Semaphores.AsyncSemaphoreReleaser throughState = default;

        // Blocks under a handle that took the epoch, and is handed the permit with a state of its own
        var waiter = BlockingThread.Start(() => throughState = semaphore.Wait());

        await waiter.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);
        await Assert.That(semaphore.ReleasesThroughEpoch).IsFalse();

        throughEpoch.Dispose();
        await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var staleThroughState = throughState;

        staleThroughEpoch.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        await Assert.That(semaphore.TryWait(out _)).IsFalse();

        throughState.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);

        // Back at rest the gate takes its epoch again, and the spent copies of both kinds stay spent
        while (!semaphore.ReleasesThroughEpoch)
        {
            (await semaphore.WaitAsync()).Dispose();
        }

        using var next = await semaphore.WaitAsync();
        staleThroughEpoch.Dispose();
        staleThroughState.Dispose();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        await Assert.That(semaphore.TryWait(out _)).IsFalse();
    }

    [Test]
    public async Task Only_A_Blocking_Wait_Takes_The_Gate_Off_Its_Epoch()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        await Assert.That(semaphore.ReleasesThroughEpoch).IsTrue();

        var holder = await semaphore.WaitAsync();

        // Neither attempts that give up at once nor queued async waits read the count in a loop
        await Assert.That(semaphore.TryWait(out _)).IsFalse();
        await Assert.That(async () => await semaphore.WaitAsync(TimeSpan.Zero)).ThrowsExactly<TimeoutException>();
        await Assert.That(() => semaphore.Wait(TimeSpan.Zero)).ThrowsExactly<TimeoutException>();

        var first = semaphore.WaitAsync();
        var second = semaphore.WaitAsync();
        holder.Dispose();
        (await first).Dispose();
        (await second).Dispose();
        await Assert.That(semaphore.ReleasesThroughEpoch).IsTrue();

        await BlockOnce(semaphore);
        await Assert.That(semaphore.ReleasesThroughEpoch).IsFalse();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Gate_With_Several_Permits_Never_Releases_Through_The_Epoch()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(2);

        for (var i = 0; i < 1_000; i++)
        {
            (await semaphore.WaitAsync()).Dispose();
        }

        await Assert.That(semaphore.ReleasesThroughEpoch).IsFalse();
    }

    /// <summary>One blocking wait behind the held permits, which is what takes an exclusive gate off its epoch for a while.</summary>
    private static async Task BlockOnce(Semaphores.AsyncSemaphore semaphore)
    {
        var holders = new List<Semaphores.AsyncSemaphoreReleaser>();

        while (semaphore.TryWait(out var holder))
        {
            holders.Add(holder);
        }

        var waiter = BlockingThread.Start(() => semaphore.Wait().Dispose());

        await waiter.WaitUntilQueued(() => semaphore.QueuedWaiterCount, 1);

        foreach (var holder in holders)
        {
            holder.Dispose();
        }

        await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void Default_Handle_Disposal_Is_Harmless()
    {
        default(Semaphores.AsyncSemaphoreReleaser).Dispose();
    }
}
