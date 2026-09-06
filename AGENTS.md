# Agent Instructions

- The library namespace is `Semaphores`.
- Use the SDK pinned in `global.json`; project files define target frameworks and compiler settings.
- Tests use TUnit. Roslyn analyzers SEM0001–SEM0004 enforce semaphore usage.
- Releaser copies must share at-most-once release state. Never pool that state: stale copies can outlive later acquisitions.

## Build and test

```bash
dotnet build AsyncSemaphore.sln
dotnet test --project AsyncSemaphore.UnitTests
dotnet test --project AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers.Tests
```

## Performance validation

- Benchmark or profile before every change and after the final edit. Prefer `AsyncSemaphore.Benchmark`; add coverage when needed to exercise affected behavior.
- Use the same representative workloads, Release configuration, machine, SDK/runtime, and measurement settings for both runs. Record the baseline revision, existing local changes, environment, and exact commands; preserve baseline and final artifacts separately.
- Compare time/throughput, allocations, GC, and relevant contention/latency. Repeat noisy measurements and fix regressions before completion; do not weaken workloads or hide unfavorable cases. Tests do not replace measurements.
- Include before/after results, percentage changes, commands, and artifact paths in the handoff and PR description. If measurements are blocked or inconclusive, explicitly leave performance validation incomplete and do not claim unchanged performance or full validation.

Run the same selection of relevant cases before and after:

```bash
dotnet run --project AsyncSemaphore.Benchmark -c Release -- --filter '<benchmark-pattern>'
```
