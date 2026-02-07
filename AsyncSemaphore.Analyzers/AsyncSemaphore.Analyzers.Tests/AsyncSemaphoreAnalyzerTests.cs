using Semaphores.Analyzers;
using Verifier = AsyncSemaphore.Analyzers.Tests.Verifiers.CSharpAnalyzerVerifier<Semaphores.Analyzers.AsyncSemaphoreAnalyzer>;

namespace AsyncSemaphore.Analyzers.Tests;

public class AsyncSemaphoreAnalyzerTests
{
    [Test]
    public async Task Must_Await_Analyzer()
    {
        const string text = @"
using Semaphores;

public class Program
{
    public void Main()
    {
        var semaphore = new AsyncSemaphore(1);
        {|#0:semaphore.WaitAsync();|}
    }
}
";

        var expected = Verifier.Diagnostic(Rules.AwaitRule).WithLocation(0);
        
        await Verifier.VerifyAnalyzerAsync(text, expected);
    }
    
    [Test]
    public async Task Must_Assign_Variable_Analyzer()
    {
        const string text = @"
using System.Threading.Tasks;
using Semaphores;

public class Program
{
    public async Task Main()
    {
        var semaphore = new AsyncSemaphore(1);
        {|#0:await semaphore.WaitAsync();|}
    }
}
";

        var expected = Verifier.Diagnostic(Rules.VariableAssignmentRule).WithLocation(0);
        
        await Verifier.VerifyAnalyzerAsync(text, expected);
    }
    
    [Test]
    public async Task Must_Use_Using_Keyword_Analyzer()
    {
        const string text = @"
using System.Threading.Tasks;
using Semaphores;

public class Program
{
    public async Task Main()
    {
        var semaphore = new AsyncSemaphore(1);
        {|#0:var lockHandle = await semaphore.WaitAsync();|}
    }
}
";

        var expected = Verifier.Diagnostic(Rules.UsingKeywordRule).WithLocation(0);
        
        await Verifier.VerifyAnalyzerAsync(text, expected);
    }
    
    [Test]
    public async Task No_Error_Flagged()
    {
        const string text = @"
using System.Threading.Tasks;
using Semaphores;

public class Program
{
    public async Task Main()
    {
        var semaphore = new AsyncSemaphore(1);
        {|#0:using var lockHandle = await semaphore.WaitAsync();|}
    }
}
";
        
        await Verifier.VerifyAnalyzerAsync(text);
    }
    
    [Test]
    public async Task No_Error_Flagged_When_Scoped()
    {
        const string text = @"
using System.Threading.Tasks;
using Semaphores;

public class Program
{
    public async Task Main()
    {
        var semaphore = new AsyncSemaphore(1);
        using (await semaphore.WaitAsync())
        {
        }
    }
}
";

        await Verifier.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task Must_Use_Using_Keyword_Via_Interface()
    {
        const string text = @"
using System.Threading.Tasks;
using Semaphores;

public class Program
{
    public async Task Main()
    {
        IAsyncSemaphore semaphore = new AsyncSemaphore(1);
        {|#0:var lockHandle = await semaphore.WaitAsync();|}
    }
}
";

        var expected = Verifier.Diagnostic(Rules.UsingKeywordRule).WithLocation(0);

        await Verifier.VerifyAnalyzerAsync(text, expected);
    }

    [Test]
    public async Task No_Error_Flagged_Via_Interface()
    {
        const string text = @"
using System.Threading.Tasks;
using Semaphores;

public class Program
{
    public async Task Main()
    {
        IAsyncSemaphore semaphore = new AsyncSemaphore(1);
        using var lockHandle = await semaphore.WaitAsync();
    }
}
";

        await Verifier.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task No_Error_For_Unrelated_Type_Named_AsyncSemaphore()
    {
        const string text = @"
using System.Threading.Tasks;

namespace OtherNamespace
{
    public class AsyncSemaphore
    {
        public Task WaitAsync() => Task.CompletedTask;
    }
}

public class Program
{
    public async Task Main()
    {
        var semaphore = new OtherNamespace.AsyncSemaphore();
        await semaphore.WaitAsync();
    }
}
";

        await Verifier.VerifyAnalyzerAsync(text);
    }
}