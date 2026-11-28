using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0012: words a person reads written in the code rather than a resource's: a literal or interpolated string with
/// a letter in it made Words (a Settings row's label, an action's title, a refusal, whatever takes Words) or given to a
/// control's words (its Text, Content, Header, Title or PlaceholderText, its tip, its accessible name or help);
/// and Words made a string by an interpolation, a + or ToString, which says them in one language at the moment written and
/// breaks the sentence a translation keeps whole; and Words said in one language (Shown, English) made Words again, which
/// an agent would read in the person's language, or a person in English. Off until a folder's .editorconfig turns it on,
/// as its area's words move into resources (src/Words.targets).</summary>
[Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnextractedWords() : HomeOnlyRule(Rules.UnextractedWord, "Passenger.Core.Words", OperationKind.Conversion,
    OperationKind.SimpleAssignment, OperationKind.Invocation, OperationKind.Interpolation, OperationKind.Binary)
{
    private static readonly string[] Shown = ["Text", "Content", "Header", "Title", "PlaceholderText"];

    protected override Func<IOperation, object[]?>? Finder(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("Passenger.Core.Words") is not { } words)
        {
            return null;
        }
        INamedTypeSymbol? control = compilation.GetTypeByMetadataName("Avalonia.AvaloniaObject");
        return operation => operation switch
        {
            IConversionOperation { OperatorMethod.ContainingType: { } type, Operand: { } from } when SymbolEqualityComparer.Default.Equals(type, words) && Written(from) =>
                [$"\"{Text(from)}\" is made Words", string.Empty],
            IConversionOperation { OperatorMethod.ContainingType: { } type, Operand: IPropertyReferenceOperation { Property.Name: "Shown" or "English", Instance.Type: { } of } said }
                when SymbolEqualityComparer.Default.Equals(type, words) && SymbolEqualityComparer.Default.Equals(of, words) =>
                [$"Words said in one language ({said.Property.Name}) are made Words again", ", passing on the Words themselves"],
            ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property: { } property }, Value: { } value }
                when Shown.Contains(property.Name) && control is not null && Derives(property.ContainingType, control) && Written(Unconverted(value)) =>
                [$"\"{Text(Unconverted(value))}\" is a control's {property.Name}", $", given through ShownWords.{property.Name}Property"],
            IInvocationOperation { TargetMethod: { Name: "SetTip" or "SetName" or "SetHelpText" } method } call
                when method.ContainingType.Name is "ToolTip" or "AutomationProperties" && call.Arguments.Length > 1 && Written(Unconverted(call.Arguments[1].Value)) =>
                [$"\"{Text(Unconverted(call.Arguments[1].Value))}\" is a control's {method.Name.Substring(3)}", $", given through ShownWords.{method.Name.Substring(3)}Property"],
            IInvocationOperation { TargetMethod.Name: "ToString", Instance.Type: { } of } when SymbolEqualityComparer.Default.Equals(of, words) =>
                ["Words are made a string", ", whose arguments put other words in"],
            IInterpolationOperation { Expression.Type: { } of } when SymbolEqualityComparer.Default.Equals(of, words) =>
                ["Words are joined into a string", ", whose arguments put other words in"],
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } join
                when SymbolEqualityComparer.Default.Equals(Unconverted(join.LeftOperand).Type, words) || SymbolEqualityComparer.Default.Equals(Unconverted(join.RightOperand).Type, words) =>
                ["Words are joined into a string", ", whose arguments put other words in"],
            _ => null,
        };
    }

    /// <summary>Whether an operation is words written in the code: a literal, or an interpolation, with a letter in its text.</summary>
    private static bool Written(IOperation operation) => Text(operation) is { } text && text.Any(char.IsLetter);

    private static string? Text(IOperation operation) => operation switch
    {
        ILiteralOperation { ConstantValue: { HasValue: true, Value: string text } } => text,
        IInterpolatedStringOperation interpolated => string.Concat(interpolated.Parts.Select(p => p is IInterpolatedStringTextOperation t ? Text(t.Text) : "{}")),
        _ => null,
    };

    private static IOperation Unconverted(IOperation operation) => operation is IConversionOperation { IsImplicit: true } conversion ? Unconverted(conversion.Operand) : operation;

    private static bool Derives(INamedTypeSymbol? type, INamedTypeSymbol from)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, from))
            {
                return true;
            }
        }
        return false;
    }
}
