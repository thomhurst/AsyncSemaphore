using NUnit.Framework;
using Verifier = AsyncSemaphore.Analyzers.Tests.Verifiers.CSharpAnalyzerVerifier<AsyncSemaphore.Analyzers.AsyncSemaphoreAnalyzer>;

namespace AsyncSemaphore.Analyzers.Tests;

public class SampleSemanticAnalyzerTests
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
}