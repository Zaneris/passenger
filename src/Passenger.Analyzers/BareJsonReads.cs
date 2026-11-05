using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0008: a JSON object's member read by System.Text.Json's own JsonElement.TryGetProperty or GetProperty anywhere
/// but inside JsonMembers. Looked at wherever JsonMembers can be reached but in a test project (one that reaches xunit's
/// Fact), whose read that throws fails its test as it should.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BareJsonReads : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.BareJsonRead];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            Compilation compilation = start.Compilation;
            if (compilation.GetTypeByMetadataName("Passenger.Core.JsonMembers") is not { } home
                || compilation.GetTypeByMetadataName("System.Text.Json.JsonElement") is not { } element
                || compilation.GetTypeByMetadataName("Xunit.FactAttribute") is not null)
            {
                return;
            }
            start.RegisterOperationAction(c =>
            {
                if (c.Operation is IInvocationOperation { TargetMethod: { Name: "TryGetProperty" or "GetProperty" } method } call
                    && Same(method.ContainingType, element) && !Within(c.ContainingSymbol, home))
                {
                    c.ReportDiagnostic(Diagnostic.Create(Rules.BareJsonRead, call.Syntax.GetLocation(), method.Name));
                }
            }, OperationKind.Invocation);
        });
    }

    private static bool Within(ISymbol? symbol, INamedTypeSymbol home)
    {
        for (ISymbol? at = symbol; at is not null; at = at.ContainingSymbol)
        {
            if (Same(at, home))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Same(ISymbol? one, ISymbol? other) => one is not null && SymbolEqualityComparer.Default.Equals(one, other);
}
