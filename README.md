# AsyncSemaphore

A simple wrapper around a `SemaphoreSlim` featuring:
- Automatic releasing without try/finally blocks by utilising the IDisposable `using` pattern
- Guarantee that release can only be called once per `WaitAsync` call
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

Benchmarks and allocations can be seen below.

```
BenchmarkDotNet v0.15.8, Linux Ubuntu 25.10 (Questing Quokka)
12th Gen Intel Core i7-12700K 3.60GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 9.0.112
  [Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v3
```


| Method                              | Mean     | Error    | StdDev   | Allocated |
|------------------------------------ |---------:|---------:|---------:|----------:|
| Raw_Semaphore_Slim                  | 31.07 ns | 0.056 ns | 0.044 ns |         - |
| AsyncSemaphore_With_Inherited_Scope | 37.25 ns | 0.255 ns | 0.238 ns |         - |
| AsyncSemaphore_With_Braced_Scope    | 37.07 ns | 0.373 ns | 0.312 ns |         - |