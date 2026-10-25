using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0006: work given to the thread pool past BackgroundWork, by Task.Run or a TaskFactory's StartNew, called
/// or named as a method group, anywhere but inside BackgroundWork itself. Looked at wherever BackgroundWork can be reached;
/// a project that cannot reach it runs where no window of the IDE's waits (the project host, the inspectors). Not looked
/// at yet (<see cref="NotYet"/>): the tests, whose own helpers start what they drive.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BareTaskRuns : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> NotYet = ImmutableHashSet.Create(StringComparer.Ordinal, "Passenger.Tests");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rules.BareTaskRun];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            Compilation compilation = start.Compilation;
            if (NotYet.Contains(compilation.AssemblyName ?? string.Empty)
                || compilation.GetTypeByMetadataName("Passenger.Core.BackgroundWork") is not { } home
                || compilation.GetTypeByMetadataName("System.Threading.Tasks.Task") is not { } task)
            {
                return;
            }
            INamedTypeSymbol? factory = compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskFactory");
            INamedTypeSymbol? factoryOf = compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskFactory`1");
            start.RegisterOperationAction(c =>
            {
                IMethodSymbol? method = c.Operation switch
                {
                    IInvocationOperation call => call.TargetMethod,
                    IMethodReferenceOperation named => named.Method,
                    _ => null,
                };
                if (method?.ContainingType?.OriginalDefinition is not { } type)
                {
                    return;
                }
                string? said = method.Name switch
                {
                    "Run" when Same(type, task) => "Task.Run",
                    "StartNew" when Same(type, factory) || Same(type, factoryOf) => "TaskFactory.StartNew",
                    _ => null,
                };
                if (said is not null && !Within(c.ContainingSymbol, home))
                {
                    c.ReportDiagnostic(Diagnostic.Create(Rules.BareTaskRun, c.Operation.Syntax.GetLocation(), said));
                }
            }, OperationKind.Invocation, OperationKind.MethodReference);
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
