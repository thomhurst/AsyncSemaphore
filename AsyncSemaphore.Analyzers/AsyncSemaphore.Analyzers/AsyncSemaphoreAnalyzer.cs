using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace AsyncSemaphore.Analyzers;

/// <summary>
/// A sample analyzer that reports invalid values being used for the 'speed' parameter of the 'SetSpeed' function.
/// To make sure that we analyze the method of the specific class, we use semantic analysis instead of the syntax tree, so this analyzer will not work if the project is not compilable.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class AsyncSemaphoreAnalyzer : DiagnosticAnalyzer
{
    private const string CommonApiClassName = "AsyncSemaphore";
    private const string CommonApiMethodName = "WaitAsync";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rules.AwaitRule, Rules.VariableAssignmentRule, Rules.UsingKeywordRule);

    public override void Initialize(AnalysisContext context)
    {
        // You must call this method to avoid analyzing generated code.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        // You must call this method to enable the Concurrent Execution.
        context.EnableConcurrentExecution();

        // Subscribe to semantic (compile time) action invocation, e.g. method invocation.
        context.RegisterOperationAction(AnalyzeOperation, OperationKind.Invocation);

        // Check other 'context.Register...' methods that might be helpful for your purposes.
    }

    /// <summary>
    /// Executed on the completion of the semantic analysis associated with the Invocation operation.
    /// </summary>
    /// <param name="context">Operation context.</param>
    private void AnalyzeOperation(OperationAnalysisContext context)
    {
        // The Roslyn architecture is based on inheritance.
        // To get the required metadata, we should match the 'Operation' and 'Syntax' objects to the particular types,
        // which are based on the 'OperationKind' parameter specified in the 'Register...' method.
        if (context.Operation is not IInvocationOperation invocationOperation ||
            context.Operation.Syntax is not InvocationExpressionSyntax invocationSyntax)
        {
            return;
        }

        var methodSymbol = invocationOperation.TargetMethod;

        // Check whether the method name is 'SetSpeed' and it is a member of the 'Spaceship' class.
        if (methodSymbol.MethodKind != MethodKind.Ordinary ||
            methodSymbol.ReceiverType?.Name != CommonApiClassName ||
            methodSymbol.Name != CommonApiMethodName
           )
        {
            return;
        }

        // Count validation is enough in most cases. Keep analyzers as simple as possible.
        if (invocationSyntax.ArgumentList.Arguments.Count != 1)
        {
            return;
        }

        var parentStatement = GetParentStatement(invocationSyntax);

        if (!parentStatement.DescendantNodes().Any(x => x is AwaitExpressionSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.AwaitRule,
                parentStatement.GetLocation()));
        }
        
        if (!parentStatement.DescendantNodes().Any(x => x is VariableDesignationSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.VariableAssignmentRule,
                parentStatement.GetLocation()));
        }
        
        if (!parentStatement.DescendantNodes().Any(x => x is UsingStatementSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rules.UsingKeywordRule,
                parentStatement.GetLocation()));
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