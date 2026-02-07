using Semaphores.Analyzers;
using Verifier = AsyncSemaphore.Analyzers.Tests.Verifiers.CSharpAnalyzerVerifier<Semaphores.Analyzers.AsyncSemaphoreReleaserAnalyzer>;

namespace AsyncSemaphore.Analyzers.Tests;

public class AsyncSemaphoreReleaserAnalyzerTests
{
    [Test]
    public async Task Do_Not_Dispose_Explicitly_Warning()
    {
        const string text = @"
using System.Threading.Tasks;
using Semaphores;

public class Program
{
    public async Task Main()
    {
        var semaphore = new AsyncSemaphore(1);
        using var lockHandle = await semaphore.WaitAsync();
        {|#0:lockHandle.Dispose()|};
    }
}
";

        var expected = Verifier.Diagnostic(Rules.DoNotDisposeExplicitlyRule).WithLocation(0);

        await Verifier.VerifyAnalyzerAsync(text, expected);
    }

    [Test]
    public async Task No_Warning_For_Unrelated_Type_Named_AsyncSemaphoreReleaser()
    {
        const string text = @"
using System;
using System.Threading.Tasks;

namespace OtherNamespace
{
    public struct AsyncSemaphoreReleaser : IDisposable
    {
        public void Dispose() { }
    }
}

public class Program
{
    public async Task Main()
    {
        var releaser = new OtherNamespace.AsyncSemaphoreReleaser();
        releaser.Dispose();
        await Task.CompletedTask;
    }
}
";

        await Verifier.VerifyAnalyzerAsync(text);
    }
}