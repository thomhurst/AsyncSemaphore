namespace AsyncSemaphore.UnitTests;

/// <summary>
/// Runs a blocking wait on its own thread, so a test can hold it blocked without starving the
/// thread pool, and can tell when it has actually parked.
/// </summary>
internal sealed class BlockingThread
{
    private static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(30);

    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BlockingThread(Action action)
    {
        _thread = new Thread(() =>
        {
            try
            {
                action();
                _completion.SetResult(true);
            }
            catch (Exception exception)
            {
                _completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
        };
    }

    /// <summary>Completes when the thread's action returns, and faults with whatever it threw.</summary>
    public Task Completion => _completion.Task;

    public static BlockingThread Start(Action action)
    {
        var blockingThread = new BlockingThread(action);
        blockingThread._thread.Start();

        return blockingThread;
    }

    public void Interrupt()
    {
        _thread.Interrupt();
    }

    /// <summary>
    /// Completes once the thread's blocking wait is parked in the waiter queue. The waiter debt appears
    /// at the commit decrement, which comes after the spin phase, and from there the thread only reads
    /// as waiting once it is past the enqueue and inside the event wait. Thread state alone is not
    /// enough: the spin phase yields, which reads as waiting too.
    /// </summary>
    public async Task WaitUntilQueued(Func<int> queuedWaiterCount, int expected)
    {
        await PollUntil(() => queuedWaiterCount() >= expected);
        await WaitUntilBlocked();
    }

    /// <summary>Completes once the thread reads as waiting, which on its own cannot tell spinning from parked.</summary>
    private Task WaitUntilBlocked()
    {
        return PollUntil(() => (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0);
    }

    private async Task PollUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + BlockTimeout;

        while (!condition())
        {
            if (_completion.Task.IsCompleted)
            {
                await _completion.Task;

                throw new InvalidOperationException("The thread finished without ever blocking.");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The thread did not block within {BlockTimeout}.");
            }

            await Task.Delay(1);
        }
    }
}
