using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0006: work given to the thread pool past BackgroundWork, by Task.Run or a TaskFactory's StartNew, called
/// or named as a method group, anywhere but inside BackgroundWork itself. Looked at wherever BackgroundWork can be reached;
/// a project that cannot reach it runs where no window of the IDE's waits (the project host, the inspectors).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BareTaskRuns() : HomeOnlyRule(Rules.BareTaskRun, "Passenger.Core.BackgroundWork", OperationKind.Invocation, OperationKind.MethodReference)
{
    protected override Func<IOperation, object[]?>? Finder(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("System.Threading.Tasks.Task") is not { } task)
        {
            return null;
        }
        INamedTypeSymbol? factory = compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskFactory");
        INamedTypeSymbol? factoryOf = compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskFactory`1");
        return operation =>
        {
            IMethodSymbol? method = operation switch
            {
                IInvocationOperation call => call.TargetMethod,
                IMethodReferenceOperation named => named.Method,
                _ => null,
            };
            if (method?.ContainingType?.OriginalDefinition is not { } type)
            {
                return null;
            }
            string? said = method.Name switch
            {
                "Run" when Symbols.Same(type, task) => "Task.Run",
                "StartNew" when Symbols.Same(type, factory) || Symbols.Same(type, factoryOf) => "TaskFactory.StartNew",
                _ => null,
            };
            return said is null ? null : [said];
        };
    }
}
