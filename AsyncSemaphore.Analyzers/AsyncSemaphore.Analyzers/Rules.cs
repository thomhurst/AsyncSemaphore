﻿using Microsoft.CodeAnalysis;

namespace AsyncSemaphore.Analyzers;

public class Rules
{
    public static DiagnosticDescriptor AwaitRule => Create("AB0001");
    public static DiagnosticDescriptor VariableAssignmentRule => Create("AB0002");
    public static DiagnosticDescriptor UsingKeywordRule => Create("AB0003");

    public static DiagnosticDescriptor Create(string diagnosticId)
    {
        var messageFormat = new LocalizableResourceString(diagnosticId + "MessageFormat",
            Resources.ResourceManager,
            typeof(Resources));
        
        var title = new LocalizableResourceString(diagnosticId + "Title",
            Resources.ResourceManager,
            typeof(Resources));
        
        var description = new LocalizableResourceString(diagnosticId + "Description",
            Resources.ResourceManager,
            typeof(Resources));

        return new(diagnosticId, title, messageFormat, "Usage",
            DiagnosticSeverity.Warning, isEnabledByDefault: true, description: description);
    }
}