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
12th Gen Intel Core i7-12700K 0.80GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
```


| Method                              | Mean     | Error    | StdDev   | Allocated |
|------------------------------------ |---------:|---------:|---------:|----------:|
| Raw_Semaphore_Slim                  | 29.95 ns | 0.054 ns | 0.048 ns |         - |
| AsyncSemaphore_With_Inherited_Scope | 35.38 ns | 0.097 ns | 0.081 ns |         - |
| AsyncSemaphore_With_Braced_Scope    | 35.72 ns | 0.247 ns | 0.231 ns |         - |