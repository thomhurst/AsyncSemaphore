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
    private const string SynchronousApiMethodName = "Wait";
    private const string TryApiMethodName = "TryWait";
    private const string LockHandleTypeName = "AsyncSemaphoreReleaser";
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

        if (methodSymbol.MethodKind == MethodKind.Ordinary && IsTryWait(methodSymbol))
        {
            if (IsTargetType(methodSymbol.ReceiverType))
            {
                AnalyzeTryWait(context, invocationOperation);
            }

            return;
        }

        // The synchronous Wait returns the same lock handle, so it gets the same handle rules minus the await.
        var isAsynchronous = methodSymbol.Name == CommonApiMethodName;

        if (methodSymbol.MethodKind != MethodKind.Ordinary ||
            (!isAsynchronous && !IsSynchronousWait(methodSymbol)))
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

        if (isAsynchronous && !descendantNodes.Any(x => x is AwaitExpressionSyntax))
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

    // TryWait hands its handle back through an out argument, and its natural shape is a guard clause
    // followed by a using, so the statement-level checks do not fit. Only a handle that can provably
    // never be disposed is reported: one that is discarded, or declared inline and never read again.
    private static void AnalyzeTryWait(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (invocation.Arguments.Length != 1)
        {
            // Code that does not compile yet
            return;
        }

        var handle = invocation.Arguments[0].Value;

        if (handle is IDeclarationExpressionOperation declaration)
        {
            handle = declaration.Expression;
        }

        if (handle is IDiscardOperation)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.VariableAssignmentRule,
                invocation.Syntax.GetLocation()));
            return;
        }

        if (handle is not ILocalReferenceOperation { IsDeclaration: true } declared)
        {
            // A variable that outlives this call: whoever owns it may dispose it.
            return;
        }

        IOperation root = invocation;

        while (root.Parent is not null)
        {
            root = root.Parent;
        }

        foreach (var operation in root.Descendants())
        {
            if (operation is ILocalReferenceOperation { IsDeclaration: false } reference
                && SymbolEqualityComparer.Default.Equals(reference.Local, declared.Local))
            {
                return;
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(Rules.UsingKeywordRule,
            invocation.Syntax.GetLocation()));
    }

    // "Wait" is a common name, so an implementer's unrelated Wait overload must not be mistaken for ours.
    private static bool IsSynchronousWait(IMethodSymbol method)
    {
        return method.Name == SynchronousApiMethodName
               && IsLockHandle(method.ReturnType);
    }

    private static bool IsTryWait(IMethodSymbol method)
    {
        return method.Name == TryApiMethodName
               && method.Parameters.Length == 1
               && method.Parameters[0].RefKind == RefKind.Out
               && IsLockHandle(method.Parameters[0].Type);
    }

    private static bool IsLockHandle(ITypeSymbol type)
    {
        return type.Name == LockHandleTypeName
               && type.ContainingNamespace?.Name == CommonNamespace;
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