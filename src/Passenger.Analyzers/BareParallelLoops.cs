using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0010: a synchronous Parallel loop (Parallel.For, ForEach or Invoke, called or named as a method group) anywhere
/// but inside ApartScheduler, which runs one on threads of its own. Looked at wherever ApartScheduler can be reached;
/// Parallel.ForEachAsync and ForAsync, whose bodies give their threads back at each wait, are passed over.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BareParallelLoops() : HomeOnlyRule(Rules.BareParallelLoop, "Passenger.Core.ApartScheduler", OperationKind.Invocation, OperationKind.MethodReference)
{
    protected override Func<IOperation, object[]?>? Finder(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("System.Threading.Tasks.Parallel") is not { } parallel)
        {
            return null;
        }
        return operation =>
        {
            IMethodSymbol? method = operation switch
            {
                IInvocationOperation call => call.TargetMethod,
                IMethodReferenceOperation named => named.Method,
                _ => null,
            };
            return method is { Name: "For" or "ForEach" or "Invoke" } && Symbols.Same(method.ContainingType?.OriginalDefinition, parallel)
                ? [$"Parallel.{method.Name}"]
                : null;
        };
    }
}
