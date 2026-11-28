using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0013: Words compared with a string: an equality or a containment (Assert.Equal, Contains, Same, Equals and
/// the like) given a string made Words, as inference makes Assert.Equal("…", words) a comparison of Words, or given a string
/// and Words both, as objects. Either compiles and holds only while the Words are verbatim, failing once they are a
/// resource's; what such a check means is the Words' English, or Words made as the compared ones are.</summary>
[Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WordsComparisons() : HomeOnlyRule(Rules.WordsComparedWithString, "Passenger.Core.Words", OperationKind.Invocation)
{
    private static readonly HashSet<string> Comparing = new(StringComparer.Ordinal)
    {
        "Equal", "NotEqual", "StrictEqual", "NotStrictEqual", "Equals", "Same", "NotSame", "Contains", "DoesNotContain",
        "Equivalent", "SequenceEqual", "Single",
    };

    protected override Func<IOperation, object[]?>? Finder(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("Passenger.Core.Words") is not { } words)
        {
            return null;
        }
        return operation => operation is IInvocationOperation call && Comparing.Contains(call.TargetMethod.Name) && Compares(call, words)
            ? [call.TargetMethod.Name]
            : null;
    }

    /// <summary>Whether the call is given a string made Words, or a string and Words, among its receiver, its arguments and
    /// the elements of a collection written as one.</summary>
    private static bool Compares(IInvocationOperation call, INamedTypeSymbol words)
    {
        bool text = false;
        bool said = false;
        foreach (IOperation value in Given(call).SelectMany(Elements))
        {
            if (value is IConversionOperation { OperatorMethod.ContainingType: { } type, Operand.Type.SpecialType: SpecialType.System_String }
                && Symbols.Same(type, words))
            {
                return true;
            }
            ITypeSymbol? of = Unconverted(value).Type;
            text |= of?.SpecialType == SpecialType.System_String;
            said |= Symbols.Same(of, words);
        }
        return text && said;
    }

    private static IEnumerable<IOperation> Given(IInvocationOperation call)
    {
        if (call.Instance is { } instance)
        {
            yield return instance;
        }
        foreach (IArgumentOperation argument in call.Arguments)
        {
            yield return argument.Value;
        }
    }

    /// <summary>The value, and each element of it where it is a collection written in place.</summary>
    private static IEnumerable<IOperation> Elements(IOperation value)
    {
        yield return value;
        IEnumerable<IOperation> elements = Unconverted(value) switch
        {
            ICollectionExpressionOperation collection => collection.Elements,
            IArrayCreationOperation { Initializer: { } initializer } => initializer.ElementValues,
            _ => [],
        };
        foreach (IOperation element in elements)
        {
            yield return element;
        }
    }

    private static IOperation Unconverted(IOperation operation) =>
        operation is IConversionOperation { IsImplicit: true } conversion ? Unconverted(conversion.Operand) : operation;
}
