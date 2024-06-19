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
BenchmarkDotNet v0.13.12, Windows 11 (10.0.22621.3593/22H2/2022Update/SunValley2)
11th Gen Intel Core i7-1185G7 3.00GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 8.0.106
  [Host]     : .NET 8.0.6 (8.0.624.26715), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.6 (8.0.624.26715), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
```


| Method                              | Mean     | Error    | StdDev   | Allocated |
|------------------------------------ |---------:|---------:|---------:|----------:|
| Raw_Semaphore_Slim                  | 38.89 ns | 0.622 ns | 0.519 ns |         - |
| AsyncSemaphore_With_Inherited_Scope | 51.36 ns | 0.466 ns | 0.413 ns |         - |
| AsyncSemaphore_With_Braced_Scope    | 51.04 ns | 0.274 ns | 0.243 ns |         - |