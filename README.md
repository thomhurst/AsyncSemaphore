# AsyncSemaphore

An async semaphore featuring:
- Automatic releasing without try/finally blocks by utilising the IDisposable `using` pattern
- At-most-once release per acquisition, even when a handle is copied, boxed, or disposed concurrently
- Pooled `IValueTaskSource` waiters to reduce allocation during contention
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

## Performance

Successful acquisitions allocate one small shared release-state object. This ensures that all copies of a handle share the same atomic release decision. Default handles and repeated disposal are harmless; disposing a stale copy cannot release a later acquisition.

Contended waits reuse pooled `IValueTaskSource` nodes. Timed or cancellable waits may additionally allocate timers, registrations, and exceptions. The implementation is not allocation-free.

Run the benchmarks for your workload and runtime:

```shell
dotnet run --project AsyncSemaphore.Benchmark -c Release -- --filter "*Benchmarks*"
```

Earlier measurements of the unprotected struct releaser do not represent the copy-safe implementation.
