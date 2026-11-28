using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0014: Words said in the person's language where an agent reads them: made a string by an interpolation, a
/// +, ToString or a string's join, given as an object (which a string's making or a builder's append says by ToString), or
/// read as Shown. An agent's answer says Words in English. Off until .editorconfig turns it on for the code answering
/// agents.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AgentsWords() : HomeOnlyRule(Rules.WordsForAgents, "Passenger.Core.Words", OperationKind.Interpolation, OperationKind.Binary,
    OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.Conversion)
{
    protected override Func<IOperation, object[]?>? Finder(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("Passenger.Core.Words") is not { } words)
        {
            return null;
        }
        return operation => operation switch
        {
            IInterpolationOperation { Expression.Type: { } of } when Symbols.Same(of, words) => ["Words are joined into a string in the person's language"],
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } join
                when Symbols.Same(Unconverted(join.LeftOperand).Type, words) || Symbols.Same(Unconverted(join.RightOperand).Type, words) => ["Words are joined into a string in the person's language"],
            IInvocationOperation { TargetMethod.Name: "ToString", Instance.Type: { } of } when Symbols.Same(of, words) => ["Words are made a string in the person's language"],
            IInvocationOperation { TargetMethod: { IsGenericMethod: true, ContainingType.SpecialType: SpecialType.System_String } method }
                when method.TypeArguments.Any(t => Symbols.Same(t, words)) => ["Words are joined into a string in the person's language"],
            IPropertyReferenceOperation { Property.Name: "Shown", Instance.Type: { } of } when Symbols.Same(of, words) => ["Words are read in the person's language (Shown)"],
            IConversionOperation { IsImplicit: true, Type.SpecialType: SpecialType.System_Object, Operand.Type: { } of, Parent: not IBinaryOperation } when Symbols.Same(of, words) =>
                ["Words are given as an object, said in the person's language"],
            _ => null,
        };
    }

    private static IOperation Unconverted(IOperation operation) =>
        operation is IConversionOperation { IsImplicit: true } conversion ? Unconverted(conversion.Operand) : operation;
}
