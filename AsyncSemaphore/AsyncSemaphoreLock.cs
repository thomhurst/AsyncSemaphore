using System;
using System.Threading;

namespace AsyncSemaphore;

internal class AsyncSemaphoreLock(SemaphoreSlim semaphoreSlim) : IDisposable
{
    public void Dispose()
    {
        semaphoreSlim.Release();
    }
}