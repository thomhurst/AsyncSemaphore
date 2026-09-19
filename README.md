# AsyncSemaphore

An async semaphore featuring:
- Automatic releasing without try/finally blocks by utilising the IDisposable `using` pattern
- At-most-once release per acquisition, even when a handle is copied, boxed, or disposed concurrently
- Pooled `IValueTaskSource` waiters to reduce allocation during contention
- `TryWait` and a blocking `Wait` alongside `WaitAsync`, plus an opt-in `UnpairedAsyncSemaphore` for signal-style use
- Analyzers to help you implement the desired pattern
- An `IAsyncSemaphore` interface for if you need to mock

## Install
`dotnet add package AsyncSemaphore`

## Usage

```csharp
private readonly AsyncSemaphore _asyncSemaphore = new AsyncSemaphore(1);

public async Task MyMethod()
{
    // Just assign the `IDisposable` returned from `WaitAsync` to a variable and use the using statement with it
    using var lockHandle = await _asyncSemaphore.WaitAsync();

    // Do whatever you want - Even if we throw exceptions, we'll release the semaphore once we leave this method's scope
    await DoSomethingInsideLock();
}
```

or scoped:

```csharp
private readonly AsyncSemaphore _asyncSemaphore = new AsyncSemaphore(1);

public async Task MyMethod()
{
    // or create your own scope with {} braces - And after you leave that scope, your lock will be released
    using (await _asyncSemaphore.WaitAsync())
    {
        await DoSomethingInsideLock();
    }

    await DoSomethingAfterLockReleased();
}
```

### Try-acquire and blocking waits

`TryWait` takes a permit only if one is available right now. It never blocks and never queues:

```csharp
if (_asyncSemaphore.TryWait(out var lockHandle))
{
    using (lockHandle)
    {
        DoSomethingInsideLock();
    }
}
```

`Wait` blocks the calling thread, for code paths where blocking is the intended behaviour. It takes the same timeout and cancellation arguments as `WaitAsync`, fails the same way (`TimeoutException`, `OperationCanceledException`), and queues in the same FIFO order as the async waiters:

```csharp
using var lockHandle = _asyncSemaphore.Wait(cancellationToken);
```

A blocked thread is woken directly by the thread that releases the permit, so it does not depend on the thread pool to make progress.

#### Upgrading a hand-written `IAsyncSemaphore`

`IAsyncSemaphore` gained `TryWait(out AsyncSemaphoreReleaser)`, `Wait(CancellationToken)` and `Wait(TimeSpan, CancellationToken)`. This is a source-breaking change for hand-written implementations: a decorator or a fake no longer compiles until it adds all three. Mocking libraries generate them on their own. A decorator forwards each one to the semaphore it wraps:

```csharp
public bool TryWait(out AsyncSemaphoreReleaser releaser) => _inner.TryWait(out releaser);

public AsyncSemaphoreReleaser Wait(CancellationToken cancellationToken = default) => _inner.Wait(cancellationToken);

public AsyncSemaphoreReleaser Wait(TimeSpan timeout, CancellationToken cancellationToken = default) => _inner.Wait(timeout, cancellationToken);
```

A fake that holds no real permits can return a `default` handle from all three, because a default handle releases nothing.

### Releasing without a prior wait

`AsyncSemaphore` only hands out a release through the handle of a successful wait, which is what lets it guarantee one release per acquisition. When that pairing genuinely does not fit (a wake-up signal, or a permit broker whose ownership is tracked elsewhere), opt in to `UnpairedAsyncSemaphore`:

```csharp
private readonly UnpairedAsyncSemaphore _signal = new UnpairedAsyncSemaphore(0);

// A waiter, with nothing to dispose
await _signal.WaitAsync(cancellationToken);

// Any other code, whether or not it ever waited
_signal.Release();
```

It has the same `WaitAsync`, `Wait` and `TryWait` operations on the same core, its count may start at zero, and its waits allocate no release handle. Nothing stops a permit from being leaked or released twice, and the analyzers do not cover it, so prefer `AsyncSemaphore` wherever the acquirer is also the releaser.

## Performance

Successful acquisitions allocate one small shared release-state object. This ensures that all copies of a handle share the same atomic release decision. Default handles and repeated disposal are harmless; disposing a stale copy cannot release a later acquisition.

Construction is cheap: the waiter queue and the node pool are created on the first contended wait, so a gate that never contends (one per cache entry, stream, or tenant) pays for neither.

Contended waits reuse pooled `IValueTaskSource` nodes. Timed or cancellable waits may additionally allocate timers, registrations, and exceptions. The implementation is not allocation-free.

Run the benchmarks for your workload and runtime:

```shell
dotnet run --project AsyncSemaphore.Benchmark -c Release -- --filter "*Benchmarks*"
```

Earlier measurements of the unprotected struct releaser do not represent the copy-safe implementation.
