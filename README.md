# AsyncSemaphore

A simple wrapper around a `SemaphoreSlim` that supports automatic releasing of a lock on scope exit with the `using` keyword.

No more `try/finally` code blocks!

Usage:

```csharp
private readonly AsyncSemaphore _asyncSemaphore = new AsyncSemaphore(1);

public async Task MyMethod()
{
    // Just assign the `IDisposable` returned from `WaitAsync` to a variable and use the using statement with it
    using var lockHandle = await semaphore.WaitAsync();

    // Do whatever you want - Even if we throw exceptions, we'll release the semaphore once we leave this method's scope
    await DoSomething();
}
```
