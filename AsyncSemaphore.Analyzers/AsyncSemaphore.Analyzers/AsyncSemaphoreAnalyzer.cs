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
    private const string CommonApiMethodName = "WaitAsync";
    private const string CommonNamespace = "Semaphores";

    private static readonly string[] ValidTypeNames = ["AsyncSemaphore", "IAsyncSemaphore"];

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
            methodSymbol.Name != CommonApiMethodName)
        {
            return;
        }

        var receiverType = methodSymbol.ReceiverType;

        if (!IsTargetType(receiverType))
        {
            return;
        }

        var parentStatement = GetParentStatement(invocationSyntax);

        if (parentStatement is null)
        {
            return;
        }

        var descendantNodes = parentStatement.DescendantNodes().ToList();
        var descendantTokens = parentStatement.DescendantTokens().ToList();

        if (!descendantNodes.Any(x => x is AwaitExpressionSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.AwaitRule,
                parentStatement.GetLocation()));
            return;
        }

        if (descendantTokens.Any(x => x.IsKind(SyntaxKind.UsingKeyword)))
        {
            // We're correct disposing it on scope exit
            return;
        }

        if (!descendantNodes.Any(x => x is VariableDeclarationSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.VariableAssignmentRule,
                parentStatement.GetLocation()));
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rules.UsingKeywordRule,
                parentStatement.GetLocation()));
    }

    private static bool IsTargetType(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.ContainingNamespace?.Name == CommonNamespace
            && ValidTypeNames.Contains(type.Name))
        {
            return true;
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.ContainingNamespace?.Name == CommonNamespace
                && iface.Name == "IAsyncSemaphore")
            {
                return true;
            }
        }

        return false;
    }

    private static SyntaxNode? GetParentStatement(InvocationExpressionSyntax invocationSyntax)
    {
        var parent = invocationSyntax.Parent;

        while (parent is not null and not StatementSyntax)
        {
            parent = parent.Parent;
        }

        return parent;
    }
}