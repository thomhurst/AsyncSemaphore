// Explicit ownership and reflection let these tests hold permits while checking retained waiters.
#pragma warning disable SEM0001, SEM0002, SEM0003, SEM0004
using System.Reflection;

namespace AsyncSemaphore.UnitTests;

public class RetentionTests
{
    [Test]
    public async Task Cancelled_Waiters_Are_Removed_While_A_Permit_Remains_Held()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        var holder = await semaphore.WaitAsync();
        var live = semaphore.WaitAsync();

        for (var i = 0; i < 10_000; i++)
        {
            using var cts = new CancellationTokenSource();
            var pending = semaphore.WaitAsync(cts.Token);
            cts.Cancel();
            await Assert.That(async () => await pending).ThrowsExactly<OperationCanceledException>();
        }

        // A live waiter ahead of cancelled nodes prevents head-only cleanup from passing.
        await Assert.That(QueueCount(semaphore, "_waiters")).IsEqualTo(1);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(0);
        holder.Dispose();
        using (await live.AsTask().WaitAsync(TimeSpan.FromSeconds(10))) { }
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Timed_Out_Waiters_Are_Removed_While_A_Permit_Remains_Held()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        using var holder = await semaphore.WaitAsync();
        var waits = Enumerable.Range(0, 100).Select(_ => semaphore.WaitAsync(TimeSpan.FromMilliseconds(10)).AsTask()).ToArray();
        foreach (var wait in waits)
        {
            await Assert.That(async () => await wait.WaitAsync(TimeSpan.FromSeconds(10))).ThrowsExactly<TimeoutException>();
        }
        await Assert.That(QueueCount(semaphore, "_waiters")).IsEqualTo(0);
    }

    [Test]
    public async Task Burst_Contention_Does_Not_Leave_An_Unbounded_Pool()
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        var holder = await semaphore.WaitAsync();
        var waits = Enumerable.Range(0, 2_000).Select(_ => semaphore.WaitAsync()).ToArray();
        holder.Dispose();
        foreach (var wait in waits)
        {
            // Direct consumption keeps returns on this thread, filling the shared overflow pool.
            using (await wait) { }
        }
        await Assert.That(QueueCount(semaphore, "_pool")).IsLessThanOrEqualTo(256);
        await Assert.That(QueueCount(semaphore, "_waiters")).IsEqualTo(0);
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    private static int QueueCount(Semaphores.AsyncSemaphore semaphore, string name)
    {
        var queue = typeof(Semaphores.AsyncSemaphore).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(semaphore)!;
        return (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
    }
}
