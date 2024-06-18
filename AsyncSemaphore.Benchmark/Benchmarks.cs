using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace AsyncSemaphore.Benchmark;

[MemoryDiagnoser]
public class Benchmarks
{
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);
    private readonly Semaphores.AsyncSemaphore _asyncSemaphore = new(1);

    [Benchmark]
    public async Task Raw_Semaphore_Slim()
    {
        try
        {
            await _semaphoreSlim.WaitAsync();
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }
    
    [Benchmark]
    public async Task AsyncSemaphore_With_Inherited_Scope()
    {
        using var _ = await _asyncSemaphore.WaitAsync();
    }
    
    [Benchmark]
    public async Task AsyncSemaphore_With_Braced_Scope()
    {
        using (await _asyncSemaphore.WaitAsync())
        {
        }
    }
}