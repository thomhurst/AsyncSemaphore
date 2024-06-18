using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Semaphores.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class AsyncSemaphoreAnalyzer : DiagnosticAnalyzer
{
    private const string CommonApiClassName = "AsyncSemaphore";
    private const string CommonApiMethodName = "WaitAsync";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.AwaitRule, Rules.VariableAssignmentRule, Rules.UsingKeywordRule);

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
            methodSymbol.ReceiverType?.Name != CommonApiClassName ||
            methodSymbol.Name != CommonApiMethodName
           )
        {
            return;
        }

        var parentStatement = GetParentStatement(invocationSyntax);

        var descendantNodes = parentStatement.DescendantNodes().ToList();
        var descendantTokens = parentStatement.DescendantTokens().ToList();
        
        if (!descendantNodes.Any(x => x is AwaitExpressionSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.AwaitRule,
                parentStatement.GetLocation()));
            return;
        }
        
        if (!descendantNodes.Any(x => x is VariableDeclarationSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.VariableAssignmentRule,
                parentStatement.GetLocation()));
            return;
        }
        
        if (!descendantTokens.Any(x => x.IsKind(SyntaxKind.UsingKeyword)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.UsingKeywordRule,
                parentStatement.GetLocation()));
            return;
        }
    }

    private static SyntaxNode GetParentStatement(InvocationExpressionSyntax invocationSyntax)
    {
        var parent = invocationSyntax.Parent;

        while (parent is not StatementSyntax)
        {
            parent = parent?.Parent;
        }
        
        return parent;
    }
}