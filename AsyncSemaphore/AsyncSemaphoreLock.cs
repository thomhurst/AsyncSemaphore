using System.Runtime.CompilerServices;

namespace Semaphores;

internal class AsyncSemaphoreLock(SemaphoreSlim semaphoreSlim) : IDisposable
{
    private bool _isDisposed;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        
        _isDisposed = true;
        
        semaphoreSlim.Release();
    }
}