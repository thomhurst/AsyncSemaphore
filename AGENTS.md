# Agent Instructions

- The library namespace is `Semaphores`.
- Use the SDK pinned in `global.json`; project files define target frameworks and compiler settings.
- Tests use TUnit. Roslyn analyzers SEM0001–SEM0004 enforce semaphore usage.
- Releaser copies must share at-most-once release state. Never pool that state: stale copies can outlive later acquisitions.
- The only state that is reused is a single-permit gate's own 64-bit epoch. A handle records the epoch it was acquired under and releases only by advancing exactly that value, which is sound because at most one acquisition is outstanding on such a gate. Do not extend it to gates with more permits, and do not shrink the epoch: it must never wrap onto a value a stale copy holds.

## Build and test

```bash
dotnet build AsyncSemaphore.sln
dotnet test --project AsyncSemaphore.UnitTests -c Release
dotnet test --project AsyncSemaphore.UnitTests.NetStandard -c Release
dotnet test --project AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers.Tests
```

- `AsyncSemaphore.UnitTests` runs once per framework the library builds for (`-f net10.0` picks one). `AsyncSemaphore.UnitTests.NetStandard` compiles the same sources against the netstandard2.0 build, the only place the `NETSTANDARD2_0` code runs; add test files to `AsyncSemaphore.UnitTests` only.
- Race tests are probabilistic. Run them in Release, the build that ships. A new guard in the lock-free core needs a test that fails when the guard is removed; check that once by removing it.
- CI runs the suite on Linux x64, Linux ARM64, Windows and macOS ARM64 (`dotnet.yml`), and `stress.yml` repeats it nightly.

## Performance validation

- Benchmark or profile before every change and after the final edit. Prefer `AsyncSemaphore.Benchmark`; add coverage when needed to exercise affected behavior.
- Use the same representative workloads, Release configuration, machine, SDK/runtime, and measurement settings for both runs. Record the baseline revision, existing local changes, environment, and exact commands; preserve baseline and final artifacts separately.
- Compare time/throughput, allocations, GC, and relevant contention/latency. Repeat noisy measurements and fix regressions before completion; do not weaken workloads or hide unfavorable cases. Tests do not replace measurements.
- Include before/after results, percentage changes, commands, and artifact paths in the handoff and PR description. If measurements are blocked or inconclusive, explicitly leave performance validation incomplete and do not claim unchanged performance or full validation.

Run the same selection of relevant cases before and after:

```bash
dotnet run --project AsyncSemaphore.Benchmark -c Release -- --filter '<benchmark-pattern>'
```
