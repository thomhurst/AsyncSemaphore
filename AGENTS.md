# Agent Instructions

- The library namespace is `Semaphores`.
- Use the SDK pinned in `global.json`; project files define target frameworks and compiler settings.
- Tests use TUnit. Roslyn analyzers SEM0001–SEM0004 enforce semaphore usage.
- Releaser copies must share at-most-once release state. Never pool that state: stale copies can outlive later acquisitions.
- The only state that is reused is a single-permit gate's own 64-bit epoch. A handle records the epoch it was acquired under and releases only by advancing exactly that value, which is sound because at most one acquisition is outstanding on such a gate. Do not extend it to gates with more permits, and do not shrink the epoch: it must never wrap onto a value a stale copy holds.
- On a single-permit gate a release publishes the waiter it granted the permit to (`WaiterQueue.InFlight`), and until that waiter's hop to the thread pool starts, an arriving caller may take the permit from it (`TryOvertake`). Exactly one of the hop and the overtaker empties the slot, and the slot only ever names a waiter whose grant is pending on that gate, which is what lets a hop that outlived its own grant run safely against a recycled node. The overtaken waiter goes back on the count as debt and is served by the overtaker's release, ahead of the queue, at most `MaxOvertakes` times. This is sound only because the overtaker then holds the only permit. Do not extend it to gates with more permits or to unpaired ones, nor to waiters that can still be cancelled or time out: claiming one switches that off, and an overtaken grant would sit out a whole critical section without it.

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
- Use the same representative workloads, Release configuration, machine, SDK/runtime, and measurement settings for both runs. Record the baseline revision, existing local changes, environment, and exact commands; preserve baseline and final artifacts separately. Build a baseline in its own worktree and keep artifacts under the evidence root; locations are in [issue-pr-loop § Isolation and ownership](.agents/skills/issue-pr-loop/SKILL.md#isolation-and-ownership).
- Compare time/throughput, allocations, GC, and relevant contention/latency. Repeat noisy measurements and fix regressions before completion; do not weaken workloads or hide unfavorable cases. Tests do not replace measurements.
- Include before/after results, percentage changes, commands, and artifact paths in the handoff and PR description. If measurements are blocked or inconclusive, explicitly leave performance validation incomplete and do not claim unchanged performance or full validation.

Run the same selection of relevant cases before and after:

```bash
dotnet run --project AsyncSemaphore.Benchmark -c Release -- --filter '<benchmark-pattern>'
```

## Pull requests

- Open every agent-created PR as a draft (`gh pr create --draft`) and follow [issue-pr-loop § Draft review lifecycle](.agents/skills/issue-pr-loop/SKILL.md#draft-review-lifecycle), even outside the loop: mark it ready only after Claude and Codex approve the current head and every finding is dispositioned, then arm auto-merge at once with `scripts/Enable-AutoMerge.ps1` (never on a draft, never `gh pr merge --auto` directly).
- Every review cycle, check both review threads (`pulls/<N>/reviews`, `pulls/<N>/comments`, GraphQL `reviewThreads`) and PR conversation comments (`issues/<N>/comments`). Reviewers differ: Claude often posts its review as a conversation comment, while other agents use review threads. A finding on either surface needs a disposition before merge.
- Resolve each PR review thread, whether a human or a bot opened it, as soon as you have dispositioned it: the fix is pushed to the PR head and your reply names the commit, or your reply pushes back on the finding with evidence. Leave a thread open only while it has no disposition. If the reviewer replies after your disposition, unresolve the thread and handle the reply.

## Autonomous self-improvement

Treat every problem you notice as work to act on, whether you find it while testing, developing, reading code, reviewing, or doing anything else. Examples include bugs, flaky or failing tests, incorrect docs, misleading comments, broken tooling, and gaps in these instructions.

- If the fix is trivial and low-risk, fix it as part of the current work and mention it in the PR description.
- If the fix is out of scope for the current work, raise a GitHub issue at the point you discover it so it can be fixed later. Include what you saw, where you saw it (file and line, command, or CI run), how to reproduce it, and the expected behavior. Search existing issues first, and add to a matching issue instead of opening a duplicate.
- Do not ignore a problem because it existed before your change or is unrelated to it.
