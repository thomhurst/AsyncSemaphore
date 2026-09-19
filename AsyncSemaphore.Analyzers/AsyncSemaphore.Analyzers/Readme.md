# AsyncSemaphore analyzers

The `AsyncSemaphore` NuGet package includes Roslyn analyzers that encourage safe acquisition and release through the `using` pattern. The acquisition checks apply to `Semaphores.AsyncSemaphore`, `Semaphores.IAsyncSemaphore`, and types implementing that interface.

## Diagnostics

All four diagnostics are warnings enabled by default.

| Diagnostic | Guidance | Applies to |
| --- | --- | --- |
| SEM0001 | Await the asynchronous wait. | `WaitAsync` only. |
| SEM0002 | Keep the acquired handle instead of discarding it. | `WaitAsync` and `Wait` without a variable assignment or `using`; `TryWait(out _)`. |
| SEM0003 | Dispose the acquired handle with `using`. | `WaitAsync` and `Wait` assigned without `using`; an inline `TryWait(out var handle)` declaration whose handle is never referenced again. |
| SEM0004 | Use `using` instead of calling `Dispose()` explicitly. | Explicit calls to `AsyncSemaphoreReleaser.Dispose()`. |

## Asynchronous and blocking waits

Use `using` with the returned handle so the permit is returned even if the protected work throws:

```csharp
using var lockHandle = await semaphore.WaitAsync(cancellationToken);
```

For an intentionally blocking operation:

```csharp
using var lockHandle = semaphore.Wait(cancellationToken);
```

A scoped `using (semaphore.Wait()) { ... }` is also supported. `Wait` does not require `await`. Its handle must still be disposed: a bare `semaphore.Wait();`, although familiar from `SemaphoreSlim`, loses the handle and leaks the permit.

The synchronous checks only apply to `Wait` methods returning `AsyncSemaphoreReleaser`. An unrelated overload such as `bool Wait(int attempts)` on an `IAsyncSemaphore` implementation is not treated as a semaphore acquisition.

## Try-acquire

`TryWait` returns a Boolean and supplies its handle through an `out` argument. Dispose the handle when acquisition succeeds:

```csharp
if (semaphore.TryWait(out var lockHandle))
{
    using (lockHandle)
    {
        DoSomethingInsideLock();
    }
}
```

A guard clause followed by `using` is also supported:

```csharp
if (!semaphore.TryWait(out var lockHandle))
{
    return;
}

using (lockHandle)
{
    DoSomethingInsideLock();
}
```

`TryWait(out _)` reports SEM0002 because the handle is discarded. `TryWait(out var lockHandle)` reports SEM0003 if that local is never referenced again in the containing operation tree.

These are limited checks, not proof of correct disposal. Referencing the handle later is enough to avoid the unused-handle warning; the analyzer does not verify disposal on every path. Passing an existing variable as the `out` argument is not checked for disposal. You remain responsible for disposing every successfully acquired handle.

## Unpaired semaphores

The analyzers do not cover `UnpairedAsyncSemaphore`. Its waits return no release handle, and `Release()` may be called by a different participant that never waited. Callers are responsible for permit accounting. Prefer `AsyncSemaphore` when the acquirer can also release the permit.

See the [library README](../../README.md) for timeouts, cancellation, unpaired usage, and upgrading an `IAsyncSemaphore` implementation.

## Development

Build the analyzer and run its TUnit tests from the repository root:

```shell
dotnet build AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers
dotnet test --project AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers.Tests
```
