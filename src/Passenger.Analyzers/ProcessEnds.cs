using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0007: .NET's Process.HasExited read anywhere but inside DotnetProcesses, a property pattern's read of it too.
/// Looked at wherever DotnetProcesses can be reached; a project that cannot reach it asks no process whether it runs.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProcessEnds() : HomeOnlyRule(Rules.ProcessEnd, "Passenger.Core.DotnetProcesses", OperationKind.PropertyReference)
{
    protected override Func<IOperation, object[]?>? Finder(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("System.Diagnostics.Process")?.GetMembers("HasExited").OfType<IPropertySymbol>().FirstOrDefault() is not { } ended)
        {
            return null;
        }
        return operation => operation is IPropertyReferenceOperation read && Symbols.Same(read.Property.OriginalDefinition, ended) ? [] : null;
    }
}
