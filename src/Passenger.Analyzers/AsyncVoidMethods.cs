using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Passenger.Analyzers;

/// <summary>PSG0002: a method or local function written async that returns void, an event handler's as much as any other.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncVoidMethods : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.AsyncVoidMethod];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Check, SyntaxKind.MethodDeclaration, SyntaxKind.LocalFunctionStatement);
    }

    private static void Check(SyntaxNodeAnalysisContext context)
    {
        (SyntaxTokenList modifiers, SyntaxToken name) = context.Node switch
        {
            MethodDeclarationSyntax method => (method.Modifiers, method.Identifier),
            LocalFunctionStatementSyntax function => (function.Modifiers, function.Identifier),
            _ => (default, default),
        };
        if (!modifiers.Any(SyntaxKind.AsyncKeyword)
            || context.SemanticModel.GetDeclaredSymbol(context.Node, context.CancellationToken) is not IMethodSymbol { ReturnsVoid: true })
        {
            return;
        }
        context.ReportDiagnostic(Diagnostic.Create(Rules.AsyncVoidMethod, name.GetLocation(), name.ValueText));
    }
}
