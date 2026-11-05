using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0007: .NET's Process.HasExited read anywhere but inside DotnetProcesses, a property pattern's read of it too.
/// Looked at wherever DotnetProcesses can be reached; a project that cannot reach it asks no process whether it runs.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProcessEnds : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.ProcessEnd];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            Compilation compilation = start.Compilation;
            if (compilation.GetTypeByMetadataName("Passenger.Core.DotnetProcesses") is not { } home
                || compilation.GetTypeByMetadataName("System.Diagnostics.Process")?.GetMembers("HasExited").OfType<IPropertySymbol>().FirstOrDefault() is not { } ended)
            {
                return;
            }
            start.RegisterOperationAction(c =>
            {
                if (c.Operation is IPropertyReferenceOperation read && Same(read.Property.OriginalDefinition, ended) && !Within(c.ContainingSymbol, home))
                {
                    c.ReportDiagnostic(Diagnostic.Create(Rules.ProcessEnd, read.Syntax.GetLocation()));
                }
            }, OperationKind.PropertyReference);
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
