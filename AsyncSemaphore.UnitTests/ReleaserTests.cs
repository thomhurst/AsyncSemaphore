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

    [Test]
    public void Default_Handle_Disposal_Is_Harmless()
    {
        default(Semaphores.AsyncSemaphoreReleaser).Dispose();
    }
}
