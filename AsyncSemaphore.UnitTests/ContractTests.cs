#pragma warning disable SEM0001, SEM0002, SEM0003, SEM0004
using System.Reflection;
using System.Runtime.Versioning;

namespace AsyncSemaphore.UnitTests;

public class ContractTests
{
    [Test]
    public async Task Tests_Load_The_Intended_Library_Target()
    {
        var framework = typeof(Semaphores.AsyncSemaphore).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
#if LIBRARY_NETSTANDARD2_0
        await Assert.That(framework).IsEqualTo(".NETStandard,Version=v2.0");
#else
        var testFramework = typeof(ContractTests).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
        await Assert.That(framework).IsEqualTo(testFramework);
#endif
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Constructor_Requires_A_Positive_Count(int count)
    {
        await Assert.That(() => new Semaphores.AsyncSemaphore(count)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(-20_000L)]
    [Arguments(21_474_836_480_000L)]
    [Arguments(long.MinValue)]
    [Arguments(long.MaxValue)]
    public async Task Invalid_Timeout_Throws_Without_Consuming_A_Permit(long ticks)
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        await Assert.That(() => { _ = semaphore.WaitAsync(TimeSpan.FromTicks(ticks)); }).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(-19_999L)]
    [Arguments(-10_000L)]
    [Arguments(21_474_836_479_999L)]
    public async Task Accepted_Timeout_Boundaries_Acquire_On_The_Queued_Path(long ticks)
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        var holder = await semaphore.WaitAsync();
        var pending = semaphore.WaitAsync(TimeSpan.FromTicks(ticks));
        await Assert.That(pending.IsCompleted).IsFalse();
        holder.Dispose();
        using (await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(10))) { }
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(-9_999L)]
    [Arguments(-1L)]
    [Arguments(0L)]
    [Arguments(1L)]
    [Arguments(9_999L)]
    public async Task Fractional_Zero_Timeout_Is_An_Immediate_Attempt(long ticks)
    {
        using var semaphore = new Semaphores.AsyncSemaphore(1);
        using var holder = await semaphore.WaitAsync();
        var pending = semaphore.WaitAsync(TimeSpan.FromTicks(ticks));
        await Assert.That(pending.IsCompleted).IsTrue();
        await Assert.That(async () => await pending).ThrowsExactly<TimeoutException>();
    }

    [Test]
    public async Task Dispose_Rejects_All_New_Waits_But_Allows_Existing_Handoff()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);
        var holder = await semaphore.WaitAsync();
        var pending = semaphore.WaitAsync();
        semaphore.Dispose();
        semaphore.Dispose();
        await Assert.That(() => { _ = semaphore.WaitAsync(); }).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => { _ = semaphore.WaitAsync(CancellationToken.None); }).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => { _ = semaphore.WaitAsync(TimeSpan.Zero); }).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => { _ = semaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None); }).ThrowsExactly<ObjectDisposedException>();
        holder.Dispose();
        using (await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(10))) { }
        await Assert.That(semaphore.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task Pending_Wait_Remains_Cancellable_After_Disposal()
    {
        var semaphore = new Semaphores.AsyncSemaphore(1);
        using var holder = await semaphore.WaitAsync();
        using var cts = new CancellationTokenSource();
        var pending = semaphore.WaitAsync(cts.Token);
        semaphore.Dispose();
        cts.Cancel();
        await Assert.That(async () => await pending).ThrowsExactly<OperationCanceledException>();
    }
}
