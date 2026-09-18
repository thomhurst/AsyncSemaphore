using System.Runtime.CompilerServices;

namespace AsyncSemaphore.Benchmark.Baseline;

/// <summary>Releases an acquired permit at most once, including when the handle is copied or boxed.</summary>
public readonly struct BaselineReleaser : IDisposable
{
    private readonly ReleaseState? _state;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal BaselineReleaser(BaselineAsyncSemaphore semaphore)
    {
        _state = new ReleaseState(semaphore);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        _state?.Dispose();
    }

    // Copies must share the release decision. This state cannot be pooled: an arbitrarily old
    // copy may still be disposed after a later acquisition has started.
    private sealed class ReleaseState
    {
        private BaselineAsyncSemaphore? _semaphore;

        public ReleaseState(BaselineAsyncSemaphore semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
