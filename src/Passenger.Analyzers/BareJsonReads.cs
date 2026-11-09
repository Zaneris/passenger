using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0008: a JSON object's member read by System.Text.Json's own JsonElement.TryGetProperty or GetProperty anywhere
/// but inside JsonMembers. Looked at wherever JsonMembers can be reached but in a test project (one that reaches xunit's
/// Fact), whose read that throws fails its test as it should.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BareJsonReads() : HomeOnlyRule(Rules.BareJsonRead, "Passenger.Core.JsonMembers", OperationKind.Invocation)
{
    protected override Func<IOperation, object[]?>? Finder(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("System.Text.Json.JsonElement") is not { } element
            || compilation.GetTypeByMetadataName("Xunit.FactAttribute") is not null)
        {
            return null;
        }
        return operation => operation is IInvocationOperation { TargetMethod: { Name: "TryGetProperty" or "GetProperty" } method }
            && Symbols.Same(method.ContainingType, element) ? [method.Name] : null;
    }
}
