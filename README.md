# AsyncSemaphore

A fast, allocation-free async semaphore featuring:
- Automatic releasing without try/finally blocks by utilising the IDisposable `using` pattern
- Guarantee that release can only be called once per `WaitAsync` call
- A custom lock-free core that outperforms `SemaphoreSlim` (~2x faster uncontended, ~27% faster async handoff) and allocates zero bytes even for contended async waits (pooled `IValueTaskSource` waiters)
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

AsyncSemaphore no longer wraps `SemaphoreSlim` — it has its own lock-free core:

- **Uncontended waits** are a single interlocked operation (no monitor lock): ~2x faster than `SemaphoreSlim`.
- **Contended async waits** use pooled (including thread-local cached) `IValueTaskSource` waiter nodes: ~27% faster handoff and zero bytes allocated per wait, versus 88 B per async waiter for `SemaphoreSlim`.

```
BenchmarkDotNet v0.15.8, Windows 11
12th Gen Intel Core i7-12700K 3.60GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11, X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.11, X64 RyuJIT x86-64-v3
```

| Method                      | Categories   | Mean      | Ratio | Allocated | Alloc Ratio |
|---------------------------- |------------- |----------:|------:|----------:|------------:|
| SemaphoreSlim               | Uncontended  |  29.73 ns |  1.00 |         - |          NA |
| AsyncSemaphore              | Uncontended  |  15.16 ns |  0.51 |         - |          NA |
|                             |              |           |       |           |             |
| SemaphoreSlim_AsyncHandoff  | AsyncHandoff |  47.60 ns |  1.00 |      88 B |        1.00 |
| AsyncSemaphore_AsyncHandoff | AsyncHandoff |  34.98 ns |  0.73 |         - |        0.00 |
|                             |              |           |       |           |             |
| SemaphoreSlim_Parallel      | Parallel     | 710.74 ns |  1.00 |      89 B |        1.00 |
| AsyncSemaphore_Parallel     | Parallel     | 747.84 ns |  1.05 |         - |        0.01 |

`Uncontended` is a wait/release cycle with the permit available. `AsyncHandoff` measures handing the permit to a queued async waiter. `Parallel` is 4 workers hammering a single-permit semaphore with an `await Task.Yield()` inside the lock — AsyncSemaphore trades ~5% mean time there for fully allocation-free waits.