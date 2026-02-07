using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Semaphores.Analyzers;

/// <summary>
/// Reports SEM0004 when Dispose is called explicitly on an AsyncSemaphoreReleaser instead of using the `using` keyword.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class AsyncSemaphoreReleaserAnalyzer : DiagnosticAnalyzer
{
    private const string CommonApiClassName = "AsyncSemaphoreReleaser";
    private const string CommonApiMethodName = "Dispose";
    private const string CommonNamespace = "Semaphores";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.DoNotDisposeExplicitlyRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeOperation, OperationKind.Invocation);
    }

    /// <summary>
    /// Executed on the completion of the semantic analysis associated with the Invocation operation.
    /// </summary>
    /// <param name="context">Operation context.</param>
    private void AnalyzeOperation(OperationAnalysisContext context)
    {
        if (context.Operation is not IInvocationOperation invocationOperation ||
            context.Operation.Syntax is not InvocationExpressionSyntax invocationSyntax)
        {
            return;
        }

        var methodSymbol = invocationOperation.TargetMethod;

        if (methodSymbol.MethodKind != MethodKind.Ordinary ||
            methodSymbol.Name != CommonApiMethodName)
        {
            return;
        }

        var receiverType = methodSymbol.ReceiverType;

        if (receiverType?.Name != CommonApiClassName ||
            receiverType.ContainingNamespace?.Name != CommonNamespace)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rules.DoNotDisposeExplicitlyRule, invocationSyntax.GetLocation()));
    }
}