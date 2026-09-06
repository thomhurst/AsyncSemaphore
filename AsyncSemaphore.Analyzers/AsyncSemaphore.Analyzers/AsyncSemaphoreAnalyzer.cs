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

        if (!IsTargetType(receiverType, context.Compilation))
        {
            return;
        }

        var location = (GetParentStatement(invocationSyntax) ?? invocationSyntax).GetLocation();
        IOperation value = SkipWrappers(invocationOperation);

        // Only known task adapters preserve ownership of this acquisition. An await elsewhere
        // in a condition, argument, or lambda does not consume this particular ValueTask.
        while (value.Parent is IInvocationOperation adapter
               && adapter.Instance == value
               && IsTaskAdapter(adapter.TargetMethod, context.Compilation))
        {
            value = SkipWrappers(adapter);
        }

        if (value.Parent is not IAwaitOperation awaited || awaited.Operation != value)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.AwaitRule, location));
            return;
        }

        var result = FollowHandleValue(awaited).Syntax;
        // Parentheses may not have their own operation in every compiler version.
        while (result.Parent is ParenthesizedExpressionSyntax)
        {
            result = result.Parent;
        }

        if (result.Parent is UsingStatementSyntax scoped && scoped.Expression == result)
        {
            return;
        }

        if (result.Parent is EqualsValueClauseSyntax initializer
            && initializer.Parent is VariableDeclaratorSyntax declarator
            && declarator.Parent is VariableDeclarationSyntax declaration)
        {
            if (declaration.Parent is LocalDeclarationStatementSyntax local
                && local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                || declaration.Parent is UsingStatementSyntax)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rules.UsingKeywordRule, location));
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rules.VariableAssignmentRule, location));
    }

    private static IOperation SkipWrappers(IOperation value)
    {
        while (value.Parent is IConversionOperation { OperatorMethod: null }
               or IParenthesizedOperation)
        {
            value = value.Parent;
        }

        return value;
    }

    private static IOperation FollowHandleValue(IOperation value)
    {
        while (true)
        {
            value = SkipWrappers(value);
            if (value.Parent is IConditionalOperation conditional
                && (conditional.WhenTrue == value || conditional.WhenFalse == value))
            {
                value = conditional;
            }
            else if (value.Parent is ISwitchExpressionArmOperation arm
                     && arm.Value == value && arm.Parent is ISwitchExpressionOperation selection)
            {
                value = selection;
            }
            else
            {
                return value;
            }
        }
    }

    private static bool IsTaskAdapter(IMethodSymbol method, Compilation compilation)
    {
        var type = method.ContainingType.OriginalDefinition;
        var valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        var task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        return (method.Name is "ConfigureAwait" or "AsTask"
                && SymbolEqualityComparer.Default.Equals(type, valueTask))
               || (method.Name == "ConfigureAwait"
                   && SymbolEqualityComparer.Default.Equals(type, task));
    }

    private static bool IsTargetType(ITypeSymbol? type, Compilation compilation)
    {
        if (type is null)
        {
            return false;
        }

        var semaphore = compilation.GetTypeByMetadataName("Semaphores.AsyncSemaphore");
        var semaphoreInterface = compilation.GetTypeByMetadataName("Semaphores.IAsyncSemaphore");
        if (SymbolEqualityComparer.Default.Equals(type, semaphore)
            || SymbolEqualityComparer.Default.Equals(type, semaphoreInterface))
        {
            return true;
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, semaphoreInterface))
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
