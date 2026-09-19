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

The library types are in the `Semaphores` namespace:

```csharp
using Semaphores;
```

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

#### Try-acquire

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

When `TryWait` returns `true`, dispose the returned handle with `using` to return the permit. When it returns `false`, no permit was taken and the returned handle is `default`; disposing it does nothing. Use `TryWait` instead of checking `CurrentCount` before a wait, because another caller can take the available permit between the check and the wait.

#### Blocking waits

`Wait` blocks the calling thread, for code paths where blocking is the intended behaviour. Prefer `WaitAsync` in async code. Calling `Wait`, or awaiting `WaitAsync`, gives you an `AsyncSemaphoreReleaser` that must be disposed with `using`. Blocking and async waiters share the same FIFO queue:

```csharp
using var lockHandle = _asyncSemaphore.Wait(cancellationToken);
```

A blocked thread is woken directly by the thread that releases the permit, so it does not depend on the thread pool to make progress.

#### Timeouts and cancellation

Both `Wait` and `WaitAsync` accept a timeout and a cancellation token. For example, a synchronous operation can wait for up to five seconds:

```csharp
public void MyBlockingMethod(CancellationToken cancellationToken)
{
    using var lockHandle = _asyncSemaphore.Wait(TimeSpan.FromSeconds(5), cancellationToken);
    DoSomethingInsideLock();
}
```

The async equivalent is `using var lockHandle = await _asyncSemaphore.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);`.

- A wait that times out throws `TimeoutException`; it does not return `false` as a timed `SemaphoreSlim.Wait` does.
- `TimeSpan.Zero` makes a single immediate attempt and throws `TimeoutException` if no permit is available. Use `TryWait` when you want a Boolean result instead.
- Omitting the timeout, or passing `Timeout.InfiniteTimeSpan`, waits without a time limit.
- Cancellation throws `OperationCanceledException`. A token that is already cancelled prevents acquisition even when a permit is available.

Timeouts and cancellation apply to acquiring the permit, not to the work inside the `using` scope. A failed wait gives you no handle to dispose. These timeout and cancellation rules also apply to `UnpairedAsyncSemaphore`.

### Upgrading a hand-written `IAsyncSemaphore`

`IAsyncSemaphore` gained `TryWait(out AsyncSemaphoreReleaser)`, `Wait(CancellationToken)` and `Wait(TimeSpan, CancellationToken)`. This is a source and binary breaking change for hand-written implementations: a decorator or a fake must add all three members and be recompiled. Mocking libraries generate the members on their own. A decorator forwards each one to the semaphore it wraps:

```csharp
public bool TryWait(out AsyncSemaphoreReleaser releaser) => _inner.TryWait(out releaser);

public AsyncSemaphoreReleaser Wait(CancellationToken cancellationToken = default) => _inner.Wait(cancellationToken);

public AsyncSemaphoreReleaser Wait(TimeSpan timeout, CancellationToken cancellationToken = default) => _inner.Wait(timeout, cancellationToken);
```

A fake that holds no real permits can return a `default` handle from both `Wait` overloads, because a default handle releases nothing. Its `TryWait` implementation must assign the `out` handle (which can also be `default`) and return a Boolean indicating the simulated acquisition result.

### Releasing without a prior wait

`AsyncSemaphore` only hands out a release through the handle of a successful wait, which is what lets it guarantee one release per acquisition. When that pairing genuinely does not fit (a wake-up signal, or a permit broker whose ownership is tracked elsewhere), opt in to `UnpairedAsyncSemaphore`:

```csharp
private readonly UnpairedAsyncSemaphore _signal = new UnpairedAsyncSemaphore(0);

// Called by the consumer. There is no release handle to dispose.
public async Task WaitForSignalAsync(CancellationToken cancellationToken)
{
    await _signal.WaitAsync(cancellationToken);
}

// Called independently by the producer, whether or not it ever waited.
public void Signal() => _signal.Release();
```

`UnpairedAsyncSemaphore` runs on the same core, but its waits return no release handle:

| Member | Result | Behaviour |
| --- | --- | --- |
| `WaitAsync` | `ValueTask` | Asynchronously consumes one permit. |
| `Wait` | `void` | Blocks the calling thread until it consumes one permit. |
| `TryWait()` | `bool` | Consumes one permit immediately if available; otherwise returns `false` without queueing. |
| `Release()` | `void` | Publishes one permit without requiring a prior wait. |
| `CurrentCount` | `int` | Reports the number of currently available permits. |

Both `WaitAsync` and `Wait` have overloads taking an optional `CancellationToken`, or a `TimeSpan` timeout and an optional `CancellationToken`. For example, `await _signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);` consumes a signal without a `using` statement. `UnpairedAsyncSemaphore` implements `IDisposable`, but not `IAsyncSemaphore`, whose waits return release handles.

Unlike `AsyncSemaphore`, whose constructor requires a positive count, `UnpairedAsyncSemaphore` accepts an initial count of zero. Each `Release()` wakes one queued waiter in FIFO order, or leaves a permit available for a future wait. Signals therefore accumulate if the producer releases before the consumer waits. There is no configurable maximum count: it can grow beyond the initial count, up to `int.MaxValue`; releasing at that limit throws `SemaphoreFullException`. There is no `Release(int)` overload.

Its waits allocate no release handle, but contention, timeouts and cancellation can still allocate. Nothing stops a permit from being leaked or released twice, and the analyzers do not cover this type, so prefer `AsyncSemaphore` wherever the acquirer is also the releaser.

## Analyzers

The `AsyncSemaphore` package includes Roslyn analyzers for `AsyncSemaphore` and `IAsyncSemaphore` usage. `Wait` gets the same handle-assignment and `using` checks as `WaitAsync`, without an `await` requirement. `TryWait` checks flag discarded or unused handles; callers must still ensure every successful acquisition is disposed.

See the [analyzer guide](AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers/Readme.md) for SEM0001–SEM0004, examples, and the limits of the `TryWait` checks.

## Performance

Successful acquisitions allocate one small shared release-state object. This ensures that all copies of a handle share the same atomic release decision. Default handles and repeated disposal are harmless; disposing a stale copy cannot release a later acquisition.

Construction is cheap: the waiter queue and the node pool are created on the first contended wait, so a gate that never contends (one per cache entry, stream, or tenant) pays for neither.

Contended waits reuse pooled `IValueTaskSource` nodes. Timed or cancellable waits may additionally allocate timers, registrations, and exceptions. The implementation is not allocation-free.

Run the benchmarks for your workload and runtime:

```shell
dotnet run --project AsyncSemaphore.Benchmark -c Release -- --filter "*Benchmarks*"
```

Earlier measurements of the unprotected struct releaser do not represent the copy-safe implementation.
