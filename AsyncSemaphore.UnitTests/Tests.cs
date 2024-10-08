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
