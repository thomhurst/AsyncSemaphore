using System;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncSemaphore;

public class AsyncSemaphore(int initialCount, int maxCount) : IDisposable
{
    private readonly SemaphoreSlim _semaphoreSlim = new(initialCount, maxCount);

    /// <inheritdoc cref="SemaphoreSlim.WaitAsync()"/>>
    public Task<IDisposable> WaitAsync(CancellationToken cancellationToken = default)
    {
        return WaitAsync(TimeSpan.FromMilliseconds(int.MaxValue), cancellationToken);
    }
    
    /// <inheritdoc cref="SemaphoreSlim.WaitAsync()"/>>
    public async Task<IDisposable> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _semaphoreSlim.WaitAsync(timeout, cancellationToken);
        return new AsyncSemaphoreLock(_semaphoreSlim);
    }

    /// <inheritdoc cref="SemaphoreSlim.CurrentCount"/>>
    public int CurrentCount => _semaphoreSlim.CurrentCount;

    /// <inheritdoc cref="IDisposable.Dispose"/>>
    public void Dispose()
    {
        _semaphoreSlim?.Dispose();
    }
}