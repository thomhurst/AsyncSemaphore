using Semaphores.Analyzers;
using Verifier = AsyncSemaphore.Analyzers.Tests.Verifiers.CSharpAnalyzerVerifier<Semaphores.Analyzers.AsyncSemaphoreAnalyzer>;

namespace AsyncSemaphore.Analyzers.Tests;

public class AcquisitionTrackingTests
{
    [Test]
    public async Task Unrelated_Await_And_Using_In_If_Body_Do_Not_Hide_Unawaited_Wait()
    {
        await Verifier.VerifyAnalyzerAsync(Wrap("""
            {|#0:if (semaphore.WaitAsync().IsCompletedSuccessfully)
            {
                using var unrelated = new System.IO.MemoryStream();
                await Task.Yield();
            }|}
            """), Verifier.Diagnostic(Rules.AwaitRule).WithLocation(0));
    }

    [Test]
    public async Task Awaiting_Another_Argument_Does_Not_Consume_The_Wait()
    {
        await Verifier.VerifyAnalyzerAsync(Wrap("""
            {|#0:Consume(semaphore.WaitAsync(), await Task.FromResult(1));|}
            """), Verifier.Diagnostic(Rules.AwaitRule).WithLocation(0));
    }

    [Test]
    public async Task Using_Another_Tuple_Element_Does_Not_Dispose_The_Handle()
    {
        await Verifier.VerifyAnalyzerAsync(Wrap("""
            {|#0:using var unrelated = (await semaphore.WaitAsync(), new System.IO.MemoryStream()).Item2;|}
            """), Verifier.Diagnostic(Rules.VariableAssignmentRule).WithLocation(0));
    }

    [Test]
    public async Task Awaiting_A_Wrapper_Does_Not_Consume_The_Wait()
    {
        await Verifier.VerifyAnalyzerAsync(Wrap("""
            {|#0:using var unrelated = await Ignore(semaphore.WaitAsync());|}
            """), Verifier.Diagnostic(Rules.AwaitRule).WithLocation(0));
    }

    [Test]
    [Arguments("using var handle = await semaphore.WaitAsync().ConfigureAwait(false);")]
    [Arguments("using var handle = await semaphore.WaitAsync().AsTask().ConfigureAwait(false);")]
    [Arguments("using (await semaphore.WaitAsync().ConfigureAwait(false)) { }")]
    [Arguments("using (var handle = await semaphore.WaitAsync()) { }")]
    [Arguments("using var handle = (await (semaphore.WaitAsync()));")]
    [Arguments("using System.IDisposable handle = await semaphore.WaitAsync();")]
    [Arguments("using var handle = System.DateTime.Now.Ticks > 0 ? await semaphore.WaitAsync() : default;")]
    [Arguments("using var handle = System.DateTime.Now.Ticks switch { 0 => default, _ => await semaphore.WaitAsync() };")]
    public async Task Direct_Resource_Ownership_Is_Accepted(string body)
    {
        await Verifier.VerifyAnalyzerAsync(Wrap(body));
    }

    [Test]
    public async Task Expression_Bodied_Wait_Is_Not_Silently_Ignored()
    {
        await Verifier.VerifyAnalyzerAsync("""
            using Semaphores;
            public class Program
            {
                public object Leak(AsyncSemaphore semaphore) => {|#0:semaphore.WaitAsync()|};
            }
            """, Verifier.Diagnostic(Rules.AwaitRule).WithLocation(0));
    }

    [Test]
    public async Task Nested_Unrelated_Namespace_Is_Not_A_Semaphore()
    {
        await Verifier.VerifyAnalyzerAsync("""
            namespace Other.Semaphores
            {
                public class AsyncSemaphore { public void WaitAsync() { } }
            }
            public class Program
            {
                public void Main() { new Other.Semaphores.AsyncSemaphore().WaitAsync(); }
            }
            """);
    }

    private static string Wrap(string body) => $$"""
        using Semaphores;
        using System.Threading.Tasks;
        public class Program
        {
            public async Task Main(AsyncSemaphore semaphore)
            {
                {{body}}
            }
            private void Consume(object value, int other) { }
            private Task<System.IO.MemoryStream> Ignore(object value) => Task.FromResult(new System.IO.MemoryStream());
        }
        """;
}
