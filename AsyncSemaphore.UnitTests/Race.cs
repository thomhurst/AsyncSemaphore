namespace AsyncSemaphore.UnitTests;

/// <summary>
/// Starts two actions at the same instant. A <see cref="Barrier"/> wakes its participants through the
/// kernel, which is microseconds of jitter; the windows raced here are tens of nanoseconds wide, so both
/// sides spin on a flag instead and the second one can be offset by a swept number of spins.
/// </summary>
internal static class Race
{
    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(30);

    public static async Task Run(Action first, Action second, int secondDelaySpins = 0)
    {
        var start = new StartFlag();

        var firstTask = Task.Run(() => start.RunWhenReleased(first, delaySpins: 0));
        var secondTask = Task.Run(() => start.RunWhenReleased(second, secondDelaySpins));

        while (!start.BothReady)
        {
            await Task.Yield();
        }

        start.Release();

        await Task.WhenAll(firstTask, secondTask).WaitAsync(RaceTimeout);
    }

    private sealed class StartFlag
    {
        private int _ready;
        private int _released;

        public bool BothReady => Volatile.Read(ref _ready) == 2;

        public void Release()
        {
            Volatile.Write(ref _released, 1);
        }

        public void RunWhenReleased(Action action, int delaySpins)
        {
            Interlocked.Increment(ref _ready);

            while (Volatile.Read(ref _released) == 0)
            {
                Thread.SpinWait(1);
            }

            if (delaySpins > 0)
            {
                Thread.SpinWait(delaySpins);
            }

            action();
        }
    }
}
