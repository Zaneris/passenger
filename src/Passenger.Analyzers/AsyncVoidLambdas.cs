using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0003: an async lambda or anonymous method converted to a delegate that returns void (`+= async (…) =>`, an
/// Action), which makes it async void.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncVoidLambdas : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.AsyncVoidLambda];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Check, OperationKind.AnonymousFunction);
    }

    private static void Check(OperationAnalysisContext context)
    {
        IAnonymousFunctionOperation lambda = (IAnonymousFunctionOperation)context.Operation;
        if (!lambda.Symbol.IsAsync || !lambda.Symbol.ReturnsVoid)
        {
            return;
        }
        string delegated = (lambda.Parent as IDelegateCreationOperation)?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)
            ?? "a void delegate";
        SyntaxToken async = lambda.Syntax switch
        {
            AnonymousFunctionExpressionSyntax function => function.AsyncKeyword,
            _ => default,
        };
        Location place = async == default ? lambda.Syntax.GetLocation() : async.GetLocation();
        context.ReportDiagnostic(Diagnostic.Create(Rules.AsyncVoidLambda, place, delegated));
    }
}
