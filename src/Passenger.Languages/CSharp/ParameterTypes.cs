using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;

namespace Passenger.Languages.CSharp;

/// <summary>A declaration's parameters as words that name its overload from its syntax alone, the same for a metadata stub
/// and the source it was built from: a type by its last name and a primitive by its keyword, however qualified or spelled;
/// a parameter passed by reference marked, its keyword aside; a type's nullable mark kept only for a primitive, the one
/// place syntax shows it makes another type.</summary>
public static class ParameterTypes
{
    public static string Of(BaseParameterListSyntax list) => string.Join(',', list.Parameters.Select(Of));

    private static string Of(ParameterSyntax parameter)
    {
        bool byReference = parameter.Modifiers.Any(m => m.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword);
        return (byReference ? "&" : string.Empty) + (parameter.Type is { } type ? Named(type) : string.Empty);
    }

    /// <summary>A type in those words.</summary>
    public static string Named(TypeSyntax type) => type switch
    {
        PredefinedTypeSyntax predefined => predefined.Keyword.Text,
        IdentifierNameSyntax name => PrimitiveTypes.Named($"System.{name.Identifier.Text}")?.CSharpName ?? name.Identifier.Text,
        QualifiedNameSyntax qualified => Named(qualified.Right),
        AliasQualifiedNameSyntax aliased => Named(aliased.Name),
        GenericNameSyntax { Identifier.Text: "Nullable", TypeArgumentList.Arguments: [var value] } => Nullable(value),
        GenericNameSyntax generic => $"{generic.Identifier.Text}<{string.Join(',', generic.TypeArgumentList.Arguments.Select(Named))}>",
        NullableTypeSyntax nullable => Nullable(nullable.ElementType),
        ArrayTypeSyntax array => Named(array.ElementType) + string.Concat(array.RankSpecifiers.Select(r => $"[{new string(',', r.Rank - 1)}]")),
        PointerTypeSyntax pointer => Named(pointer.ElementType) + "*",
        TupleTypeSyntax tuple => $"({string.Join(',', tuple.Elements.Select(e => Named(e.Type)))})",
        _ => type.ToString(),
    };

    private static string Nullable(TypeSyntax type)
    {
        string named = Named(type);
        return PrimitiveTypes.OfKeyword(named) is { Keyword: not ("string" or "object") } ? named + "?" : named;
    }
}
