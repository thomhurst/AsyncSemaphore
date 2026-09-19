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

    [Test]
    public async Task Synchronous_Wait_Must_Assign_Variable()
    {
        const string text = @"
using Semaphores;

public class Program
{
    public void Main()
    {
        var semaphore = new AsyncSemaphore(1);
        {|#0:semaphore.Wait();|}
    }
}
";

        var expected = Verifier.Diagnostic(Rules.VariableAssignmentRule).WithLocation(0);

        await Verifier.VerifyAnalyzerAsync(text, expected);
    }

    [Test]
    public async Task Synchronous_Wait_Must_Use_Using_Keyword()
    {
        const string text = @"
using Semaphores;

public class Program
{
    public void Main()
    {
        var semaphore = new AsyncSemaphore(1);
        {|#0:var lockHandle = semaphore.Wait();|}
    }
}
";

        var expected = Verifier.Diagnostic(Rules.UsingKeywordRule).WithLocation(0);

        await Verifier.VerifyAnalyzerAsync(text, expected);
    }

    [Test]
    public async Task Synchronous_Wait_Must_Use_Using_Keyword_Via_Interface()
    {
        const string text = @"
using System;
using Semaphores;

public class Program
{
    public void Main()
    {
        IAsyncSemaphore semaphore = new AsyncSemaphore(1);
        {|#0:var lockHandle = semaphore.Wait(TimeSpan.FromSeconds(1));|}
    }
}
";

        var expected = Verifier.Diagnostic(Rules.UsingKeywordRule).WithLocation(0);

        await Verifier.VerifyAnalyzerAsync(text, expected);
    }

    [Test]
    public async Task Synchronous_Wait_No_Error_Flagged()
    {
        const string text = @"
using Semaphores;

public class Program
{
    public void Main()
    {
        var semaphore = new AsyncSemaphore(1);
        using var lockHandle = semaphore.Wait();
    }
}
";

        await Verifier.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task Synchronous_Wait_No_Error_Flagged_When_Scoped()
    {
        const string text = @"
using Semaphores;

public class Program
{
    public void Main()
    {
        var semaphore = new AsyncSemaphore(1);
        using (semaphore.Wait())
        {
        }
    }
}
";

        await Verifier.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task No_Error_For_Unrelated_Wait_On_An_Implementer()
    {
        const string text = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Semaphores;

public class Decorator : IAsyncSemaphore
{
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync() => default;
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout) => default;
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync(CancellationToken cancellationToken) => default;
    public ValueTask<AsyncSemaphoreReleaser> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => default;
    public bool TryWait(out AsyncSemaphoreReleaser releaser) { releaser = default; return false; }
    public AsyncSemaphoreReleaser Wait(CancellationToken cancellationToken = default) => default;
    public AsyncSemaphoreReleaser Wait(TimeSpan timeout, CancellationToken cancellationToken = default) => default;
    public int CurrentCount => 0;
    public void Dispose() { }

    public bool Wait(int attempts) => attempts > 0;
}

public class Program
{
    public void Main()
    {
        var semaphore = new Decorator();
        semaphore.Wait(3);
    }
}
";

        await Verifier.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task TryWait_No_Error_Flagged()
    {
        const string text = @"
using Semaphores;

public class Program
{
    public void Main()
    {
        var semaphore = new AsyncSemaphore(1);

        if (semaphore.TryWait(out var lockHandle))
        {
            using (lockHandle)
            {
            }
        }
    }
}
";

        await Verifier.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task Unpaired_Semaphore_No_Error_Flagged()
    {
        const string text = @"
using System.Threading.Tasks;
using Semaphores;

public class Program
{
    public async Task Main()
    {
        var semaphore = new UnpairedAsyncSemaphore(0);
        semaphore.Release();
        await semaphore.WaitAsync();
        semaphore.Release();
        semaphore.Wait();
    }
}
";

        await Verifier.VerifyAnalyzerAsync(text);
    }
}