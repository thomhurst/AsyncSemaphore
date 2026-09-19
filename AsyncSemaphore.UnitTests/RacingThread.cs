namespace AsyncSemaphore.UnitTests;

/// <summary>
/// A dedicated thread that fires one action per round, a swept number of spins after <see cref="Fire"/>.
/// The windows raced with it are tens of nanoseconds wide. A <see cref="Barrier"/> wakes its participants
/// through the kernel and a task waits its turn in the pool's queue, which is microseconds to
/// milliseconds of jitter; spinning on pool threads instead starves a small machine's pool and every
/// other test with it. So the thread is its own, stays hot between rounds, and the caller goes straight
/// from <see cref="Fire"/> into its own side of the race.
/// </summary>
internal sealed class RacingThread : IDisposable
{
    private const int Idle = 0;
    private const int Fired = 1;
    private const int Done = 2;
    private const int Stopped = 3;

    /// <summary>Checks made flat out before the wait starts yielding: covers the caller's bookkeeping between rounds.</summary>
    private const int HotChecks = 4_000;

    /// <summary>Checks after which the other side is clearly not coming back soon, so the wait starts sleeping.</summary>
    private const int YieldingChecks = 100_000;

    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);

    private readonly Thread _thread;

    // Written by the caller before it publishes Fired, read by the thread after it observes Fired.
    private Action? _action;
    private int _delaySpins;

    // Written by the thread before it publishes Done, read by the caller after it observes Done.
    private Exception? _failure;

    private int _state;

    public RacingThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
        };

        _thread.Start();
    }

    /// <summary>Starts the round: <paramref name="action"/> runs on the racing thread <paramref name="delaySpins"/> spins from now.</summary>
    public void Fire(Action action, int delaySpins)
    {
        _action = action;
        _delaySpins = delaySpins;

        Volatile.Write(ref _state, Fired);
    }

    /// <summary>Returns once the action has returned, and rethrows what it threw.</summary>
    public void Join()
    {
        var deadline = DateTime.UtcNow + JoinTimeout;

        for (var checks = 0; Volatile.Read(ref _state) != Done; checks++)
        {
            Pause(checks);

            // Only once the wait is sleeping, where reading the clock costs nothing next to the pause
            if (checks >= YieldingChecks && DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The racing thread's action did not return within {JoinTimeout}.");
            }
        }

        var failure = _failure;
        _failure = null;

        Volatile.Write(ref _state, Idle);

        if (failure is not null)
        {
            throw new InvalidOperationException("The racing thread's action threw.", failure);
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _state, Stopped);
        _thread.Join();
    }

    private void Run()
    {
        var checks = 0;

        while (true)
        {
            var state = Volatile.Read(ref _state);

            if (state == Stopped)
            {
                return;
            }

            if (state != Fired)
            {
                Pause(checks++);

                continue;
            }

            checks = 0;

            if (_delaySpins > 0)
            {
                Thread.SpinWait(_delaySpins);
            }

            try
            {
                _action!();
            }
            catch (Exception exception)
            {
                _failure = exception;
            }

            // Not a plain write: a caller that failed mid-round disposes without joining, and its
            // Stopped must survive the action finishing behind it
            Interlocked.CompareExchange(ref _state, Done, Fired);
        }
    }

    /// <summary>Hot at first so the reaction to a state change is a few nanoseconds, then yielding and finally sleeping so a long wait costs nobody a core.</summary>
    private static void Pause(int checks)
    {
        if (checks < HotChecks)
        {
            Thread.SpinWait(1);
        }
        else if (checks < YieldingChecks)
        {
            Thread.Yield();
        }
        else
        {
            Thread.Sleep(1);
        }
    }
}
