# AsyncSemaphore

A simple wrapper around a `SemaphoreSlim` that supports automatic releasing of a lock on scope exit with the `using` keyword.

No more `try/finally` code blocks!

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
| Raw_Semaphore_Slim                  | 39.65 ns | 0.777 ns | 1.402 ns |         - |
| AsyncSemaphore_With_Inherited_Scope | 46.23 ns | 0.464 ns | 0.411 ns |         - |
| AsyncSemaphore_With_Braced_Scope    | 44.91 ns | 0.500 ns | 0.444 ns |         - |