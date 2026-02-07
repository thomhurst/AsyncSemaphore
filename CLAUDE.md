# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AsyncSemaphore is a .NET library that wraps `SemaphoreSlim` with automatic release via the `IDisposable` `using` pattern. It includes Roslyn analyzers (SEM0001–SEM0004) to enforce correct usage. The namespace is `Semaphores` (not `AsyncSemaphore`).

## Build & Test Commands

```bash
# Build the entire solution
dotnet build AsyncSemaphore.sln

# Run all unit tests (TUnit framework)
dotnet test AsyncSemaphore.UnitTests
dotnet test AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers.Tests

# Run a single test by name
dotnet test AsyncSemaphore.UnitTests --filter "Can_Enter_Immediately"

# Run benchmarks (manual only)
dotnet run --project AsyncSemaphore.Benchmark -c Release
```

The CI pipeline (`AsyncSemaphore.Pipeline` project) orchestrates builds via ModularPipelines but is not used for local development.

## Architecture

**Core library** (`AsyncSemaphore/`, namespace `Semaphores`):
- `AsyncSemaphore` — sealed class implementing `IAsyncSemaphore`. Wraps a `SemaphoreSlim` and returns `AsyncSemaphoreReleaser` from `WaitAsync()` overloads.
- `AsyncSemaphoreReleaser` — **struct** implementing `IDisposable`. Uses `Interlocked.Exchange` to guarantee single-release semantics. Zero-allocation design.
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
