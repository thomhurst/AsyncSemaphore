using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Reservoir;

namespace AsyncSemaphore.Benchmark;

/// <summary>
/// Compares the hand-rolled three-tier waiter pool used inside AsyncSemaphore
/// (thread-static slot -> instance slot -> ConcurrentQueue) against the Reservoir
/// object pool, both in isolation and integrated into the async handoff path.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PoolComparisonBenchmarks
{
    private const int HandoffOperations = 1_000;
    private const int ParallelWorkers = 4;
    private const int ParallelOperationsPerWorker = 250;

    private readonly CustomThreeTierPool _customPool = new();
    private readonly ObjectPool<Node, NodePolicy> _reservoirPool = new(default, 128, threadLocalFastPath: true);

    private readonly Semaphores.AsyncSemaphore _asyncSemaphore = new(1);
    private readonly ReservoirPooledAsyncSemaphore _reservoirSemaphore = new(1);

    public sealed class Node
    {
        public object? Owner;
        public int Value;
    }

    private struct NodePolicy : IPooledObjectPolicy<Node>
    {
        public Node Create() => new();

        public bool TryReset(Node obj) => true;

        public void Destroy(Node obj)
        {
        }
    }

    /// <summary>Mirror of the pool tiers inside AsyncSemaphore, extracted for isolation.</summary>
    private sealed class CustomThreeTierPool
    {
        [ThreadStatic]
        private static Node? t_cached;

        private readonly ConcurrentQueue<Node> _queue = new();
        private Node? _slot;

        public Node Rent()
        {
            var node = t_cached;

            if (node is not null)
            {
                t_cached = null;
            }
            else if ((node = Interlocked.Exchange(ref _slot, null)) is null && !_queue.TryDequeue(out node))
            {
                node = new Node();
            }

            node.Owner = this;

            return node;
        }

        public void Return(Node node)
        {
            if (t_cached is null)
            {
                node.Owner = null;
                t_cached = node;

                return;
            }

            if (Interlocked.CompareExchange(ref _slot, node, null) != null)
            {
                _queue.Enqueue(node);
            }
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("PoolMicro")]
    public int CustomPool_RentReturn()
    {
        var node = _customPool.Rent();
        node.Value++;
        var value = node.Value;
        _customPool.Return(node);

        return value;
    }

    [Benchmark]
    [BenchmarkCategory("PoolMicro")]
    public int Reservoir_RentReturn()
    {
        var node = _reservoirPool.Rent();
        node.Value++;
        var value = node.Value;
        _reservoirPool.Return(node);

        return value;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("PoolMicroParallel")]
    public Task CustomPool_RentReturn_Parallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                var node = _customPool.Rent();
                node.Value++;
                _customPool.Return(node);
            }
        })));
    }

    [Benchmark(OperationsPerInvoke = ParallelWorkers * ParallelOperationsPerWorker)]
    [BenchmarkCategory("PoolMicroParallel")]
    public Task Reservoir_RentReturn_Parallel()
    {
        return Task.WhenAll(Enumerable.Range(0, ParallelWorkers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < ParallelOperationsPerWorker; i++)
            {
                var node = _reservoirPool.Rent();
                node.Value++;
                _reservoirPool.Return(node);
            }
        })));
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("PooledHandoff")]
    public async Task CurrentPool_AsyncHandoff()
    {
        var holder = await _asyncSemaphore.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _asyncSemaphore.WaitAsync();
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }

    [Benchmark(OperationsPerInvoke = HandoffOperations)]
    [BenchmarkCategory("PooledHandoff")]
    public async Task ReservoirPool_AsyncHandoff()
    {
        var holder = await _reservoirSemaphore.WaitAsync();

        for (var i = 0; i < HandoffOperations; i++)
        {
            var pending = _reservoirSemaphore.WaitAsync();
            holder.Dispose();
            holder = await pending;
        }

        holder.Dispose();
    }
}
