using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Passenger.Analyzers;

/// <summary>PSG0012: words a person reads written in the code rather than a resource's: a literal or interpolated string with
/// a letter in it made Words (a Settings row's label, an action's title, a refusal, whatever takes Words) or given to a
/// control's words (its Text, Content, Header, Title or PlaceholderText, its tip, its accessible name or help);
/// and Words made a string by an interpolation, a + or ToString, which says them in one language at the moment written and
/// breaks the sentence a translation keeps whole; and Words said in one language (Shown, English) made Words again, which
/// an agent would read in the person's language, or a person in English. Off until a folder's .editorconfig turns it on,
/// as its area's words move into resources (src/Words.targets). A sweep asks for more there (<see cref="SweepOption"/>):
/// an exception's message made Words, and any other string made Words as it is.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnextractedWords() : HomeOnlyRule(Rules.UnextractedWord, "Passenger.Core.Words", OperationKind.Conversion,
    OperationKind.SimpleAssignment, OperationKind.Invocation, OperationKind.Interpolation, OperationKind.Binary)
{
    /// <summary>The .editorconfig option naming what a sweep of a folder's words asks for besides, apart by commas:
    /// <c>messages</c>, an exception's message made Words, which keeps a refusal's words in one language where
    /// RefusedException.Of keeps them whole; <c>verbatim</c>, any other string but a literal or a name made Words as it
    /// is, so words a helper wrote in English show where they become Words.</summary>
    public const string SweepOption = "passenger_words_sweep";

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

    protected override Func<IOperation, AnalyzerConfigOptions, object[]?>? Asked(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("Passenger.Core.Words") is not { } words || compilation.GetTypeByMetadataName("System.Exception") is not { } exception)
        {
            return null;
        }
        return (operation, options) =>
        {
            if (operation is not IConversionOperation { OperatorMethod.ContainingType: { } type, Operand: { Type.SpecialType: SpecialType.System_String } operand }
                || !SymbolEqualityComparer.Default.Equals(type, words) || Unconverted(operand) is ILiteralOperation or IPropertyReferenceOperation { Property.Name: "Shown" or "English" }
                || !options.TryGetValue(SweepOption, out string? asked))
            {
                return null;
            }
            string[] sweeps = [.. asked.Split(',').Select(a => a.Trim())];
            bool message = Unconverted(operand) is IPropertyReferenceOperation { Property.Name: "Message", Instance.Type: { } of } && Derives(of as INamedTypeSymbol, exception);
            if (!message && (Named(Unconverted(operand)) || Unconverted(operand) is IFieldReferenceOperation { Field: { Name: "Empty", ContainingType.SpecialType: SpecialType.System_String } }))
            {
                return null;
            }
            string written = operand.Syntax.ToString() is { Length: > 60 } text ? text.Substring(0, 60) + "…" : operand.Syntax.ToString();
            return message && sweeps.Contains("messages") ? [$"{written}, an exception's message, is made Words", ", or a refusal's words through RefusedException.Of"]
                : !message && sweeps.Contains("verbatim") ? [$"{written} is made Words as it is", ", or Words.Verbatim where it is a name, said as written in every language"]
                : null;
        };
    }

    /// <summary>Whether an operation reads a name (a variable or member called name or ending in Name), said as written.</summary>
    private static bool Named(IOperation operation) => (operation switch
    {
        ILocalReferenceOperation local => local.Local.Name,
        IParameterReferenceOperation parameter => parameter.Parameter.Name,
        IMemberReferenceOperation member => member.Member.Name,
        _ => null,
    }) is { } name && (name == "name" || name.EndsWith("Name", StringComparison.Ordinal));

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
