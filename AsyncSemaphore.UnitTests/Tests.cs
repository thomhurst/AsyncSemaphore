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

    private Task DoSomething()
    {
        return Task.Delay(500);
    }

    public static IEnumerable<int> LoopCounts() => Enumerable.Range(0, 10);

    private async Task<TimeSpan> Measure(Func<Task> func)
    {
        var start = DateTime.Now;

        await func();

        return DateTime.Now - start;
    } 
}
