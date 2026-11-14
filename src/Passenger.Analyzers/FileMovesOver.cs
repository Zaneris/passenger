using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0009: a file moved over another by .NET's File.Move or FileInfo.MoveTo, its overwrite not the constant false,
/// called or named as a method group, anywhere but inside AtomicFile, whose Move does it. Looked at wherever AtomicFile can
/// be reached but in a test project (one that reaches xunit's Fact), whose moves stand for another program's.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FileMovesOver() : HomeOnlyRule(Rules.FileMoveOver, "Passenger.Core.AtomicFile", OperationKind.Invocation, OperationKind.MethodReference)
{
    protected override Func<IOperation, object[]?>? Finder(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("System.IO.File") is not { } file || compilation.GetTypeByMetadataName("System.IO.FileInfo") is not { } info
            || compilation.GetTypeByMetadataName("Xunit.FactAttribute") is not null)
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
            string? said = method switch
            {
                { Name: "Move", Parameters.Length: 3 } when Symbols.Same(method.ContainingType, file) => "File.Move",
                { Name: "MoveTo", Parameters.Length: 2 } when Symbols.Same(method.ContainingType, info) => "FileInfo.MoveTo",
                _ => null,
            };
            bool kept = operation is IInvocationOperation invoked
                && invoked.Arguments.Any(a => a.Parameter?.Type.SpecialType == SpecialType.System_Boolean && a.Value.ConstantValue is { HasValue: true, Value: false });
            return said is null || kept ? null : [said];
        };
    }
}
