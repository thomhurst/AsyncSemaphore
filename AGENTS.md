# Agent Instructions

These instructions apply to every working agent and all changes in this repository.

## Mandatory performance validation

- Always benchmark or profile before and after making changes. Capture a baseline from the unchanged code before editing, and repeat the measurement against the final changed code before declaring the work complete.
- Use representative workloads that exercise the affected behavior. Prefer the existing `AsyncSemaphore.Benchmark` project; add or adapt benchmark coverage, or use an appropriate profiler, when existing benchmarks do not cover the change. Run the same measurement harness against both versions.
- Keep comparisons reproducible: use Release builds, the same machine, SDK/runtime, configuration, workload, and benchmark or profiler settings. Record the baseline revision and any existing local changes, exact commands, environment, and results. Preserve baseline and final artifacts separately.
- Compare relevant metrics, including execution time or throughput, allocations and GC activity, and contention or latency where applicable. Investigate apparent regressions and repeat noisy or inconclusive measurements until the comparison is reliable. Passing tests or reasoning about the code does not replace performance measurements.
- Ensure performance does not regress. Fix any regression and rerun the measurements before completing the change. Do not hide a regression by weakening workloads, dropping unfavorable cases, or averaging away an affected scenario.
- Include the before-and-after results, percentage changes, commands, and artifact locations in the final handoff and pull request description. If measurement is blocked or inconclusive, report the limitation explicitly and leave performance validation incomplete; do not claim that performance is unchanged or that the work is fully validated.

Run the benchmark project with:

```bash
dotnet run --project AsyncSemaphore.Benchmark -c Release
```

Select the relevant benchmark cases and use the same selection for both runs.

## Project Overview

AsyncSemaphore is a .NET library providing a custom lock-free async semaphore with automatic release via the `IDisposable` `using` pattern. It includes Roslyn analyzers (SEM0001–SEM0004) to enforce correct usage. The namespace is `Semaphores` (not `AsyncSemaphore`).

## Build & Test Commands

```bash
# Build the entire solution
dotnet build AsyncSemaphore.sln

# Run all unit tests (TUnit framework)
dotnet test AsyncSemaphore.UnitTests
dotnet test AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers.Tests

# Run a single test by name
dotnet test AsyncSemaphore.UnitTests --filter "Can_Enter_Immediately"

# Run benchmarks (required before and after changes; see AGENTS.md)
dotnet run --project AsyncSemaphore.Benchmark -c Release
```

The CI pipeline (`AsyncSemaphore.Pipeline` project) orchestrates builds via ModularPipelines but is not used for local development.

## Architecture

**Core library** (`AsyncSemaphore/`, namespace `Semaphores`):
- `AsyncSemaphore` — sealed class implementing `IAsyncSemaphore`. Custom lock-free core (no `SemaphoreSlim`): an `Interlocked` counter where negative values represent queued waiters, a `ConcurrentQueue` of pooled `IValueTaskSource` waiter nodes (pooled waiter nodes; each acquisition still allocates release state), and CAS-arbitrated cancellation/timeout. Cancelled nodes stay queued as dead entries; a release settles them via a compensation increment. Returns `AsyncSemaphoreReleaser` from `WaitAsync()` overloads.
- `AsyncSemaphoreReleaser` — readonly struct implementing `IDisposable`. Each acquisition allocates shared release state; an atomic exchange guarantees at-most-once release across copied, boxed, and concurrently disposed handles. Release state must not be pooled because stale copies can outlive later acquisitions.
- `IAsyncSemaphore` — interface for DI/mocking.

**Roslyn analyzers** (`AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers/`):
- `AsyncSemaphoreAnalyzer` — reports SEM0001 (must await), SEM0002 (must assign to variable), SEM0003 (must use `using`).
- `AsyncSemaphoreReleaserAnalyzer` — reports SEM0004 (do not call Dispose explicitly).
- Rules are defined in `Rules.cs` with localized strings in `Resources.resx`.
- Analyzer tests use `Microsoft.CodeAnalysis.Testing` with custom `CSharpAnalyzerVerifier` wrappers.

**Multi-targeting**: The main library targets `netstandard2.0`, `net8.0`, and `net9.0`. Analyzers target `netstandard2.0`.

## Key Conventions

- .NET SDK 9.0 (pinned in `global.json`)
- Test framework: TUnit (not xUnit/NUnit/MSTest)
- `LangVersion: preview` and `Nullable: enable` are set in `Directory.Build.props`
- StyleCop is enabled with errors treated as errors (not warnings)
- The analyzer project is referenced as an `OutputItemType="Analyzer"` in both the main library and test projects
