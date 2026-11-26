using System.Collections.Immutable;
using Humanizer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics.Analyzers.NamingStyles;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.NamingStyles;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>Names the symbol at a declaration or use from what it holds and the naming rules where it is declared.</summary>
public sealed class RenameSuggestions : IExtensionDocumentMessageHandler<Caret, string[]>
{
    private const int CandidatePoolLimit = 24;

    public async Task<string[]> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int position = WrittenPlaces.Offset(text, message.At);
        ISymbol? symbol = (await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false))?.OriginalDefinition;
        ITypeSymbol? type = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol { ContainingType.TypeKind: not TypeKind.Enum } field => field.Type,
            IPropertySymbol { IsIndexer: false } property => property.Type,
            _ => null,
        };
        if (type is null || type.TypeKind == TypeKind.Error ||
            symbol?.DeclaringSyntaxReferences.FirstOrDefault() is not { } declared ||
            document.Project.Solution.GetDocument(declared.SyntaxTree) is not { } declaring ||
            await declaring.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return [];
        }

        SyntaxNode syntax = await declared.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
        NamingStylePreferences preferences = await declaring.GetNamingStylePreferencesAsync(cancellationToken).ConfigureAwait(false);
        NamingStyle style = preferences.Rules.TryGetApplicableRule(symbol, out NamingRule rule) ? rule.NamingStyle : new(Guid.Empty,
            capitalizationScheme: symbol is IPropertySymbol ? Capitalization.PascalCase : Capitalization.CamelCase);
        ExpressionSyntax? source = syntax switch
        {
            VariableDeclaratorSyntax variable => variable.Initializer?.Value,
            PropertyDeclarationSyntax property => property.Initializer?.Value ?? property.ExpressionBody?.Expression,
            _ => null,
        };

        ImmutableArray<string> fromValue = FromValue(model, source, cancellationToken);
        IEnumerable<ImmutableArray<string>> seeds = FromType(WithoutNullable(type), fromValue);
        return (fromValue.IsEmpty ? seeds : seeds.Prepend(fromValue))
            .Select(style.CreateName)
            .Where(name => name != symbol.Name && SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !Taken(model, syntax, symbol, name))
            .Take(CandidatePoolLimit)
            .ToArray();
    }

    private static bool Taken(SemanticModel model, SyntaxNode syntax, ISymbol symbol, string name) =>
        symbol is ILocalSymbol or IParameterSymbol
            ? (syntax.Parent is BaseParameterListSyntax parameters && parameters.Parameters.Any(other => other.Identifier.ValueText == name)) ||
                model.LookupSymbols(syntax.SpanStart, name: name).Any(other => other is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
            : symbol.ContainingType.GetMembers(name).Length > 0 || symbol.ContainingType.Name == name;

    private static ImmutableArray<string> FromValue(SemanticModel model, ExpressionSyntax? value, CancellationToken cancel)
    {
        switch (value)
        {
            case AwaitExpressionSyntax awaited:
                return FromValue(model, awaited.Expression, cancel);
            case ParenthesizedExpressionSyntax grouped:
                return FromValue(model, grouped.Expression, cancel);
            case CastExpressionSyntax cast:
                return FromValue(model, cast.Expression, cancel);
            case InvocationExpressionSyntax call when model.GetSymbolInfo(call, cancel).Symbol is IMethodSymbol { MethodKind: not MethodKind.DelegateInvoke } method:
                ImmutableArray<string> words = Words(method.Name);
                if (words is not ["Get" or "Find" or "Load" or "Read" or "Fetch" or "Create" or "Build", ..])
                {
                    return [];
                }
                words = words.RemoveAt(0);
                if (words is [.., "Async"])
                {
                    words = words.RemoveAt(words.Length - 1);
                }
                return words is ["All"] or ["Value"] or ["Result"] or ["Items"] or ["By", ..] ? [] : words;
            case IdentifierNameSyntax or MemberAccessExpressionSyntax
                when model.GetSymbolInfo(value, cancel).Symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol:
                return Words(value is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText : ((IdentifierNameSyntax)value).Identifier.ValueText);
            default:
                return [];
        }
    }

    private static IEnumerable<ImmutableArray<string>> FromType(ITypeSymbol type, ImmutableArray<string> fromValue)
    {
        if (type is IArrayTypeSymbol array)
        {
            return CollectionNames(array.ElementType, "Array", fromValue);
        }
        // A string's character enumeration would suggest a name for its characters.
        if (type is not INamedTypeSymbol named || named.SpecialType == SpecialType.System_String)
        {
            return [];
        }
        ImmutableArray<INamedTypeSymbol> shapes = [named, .. named.AllInterfaces];
        if (shapes.FirstOrDefault(shape => shape.Name is "IDictionary" or "IReadOnlyDictionary" &&
            shape.Arity == 2 && IsGenericCollection(shape)) is { } dictionary)
        {
            return DictionaryNames(dictionary);
        }
        if (shapes.FirstOrDefault(shape => shape.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) is { } enumerable)
        {
            return CollectionNames(enumerable.TypeArguments[0], CollectionRole(named, shapes), fromValue);
        }

        return named.SpecialType == SpecialType.None ? NamedTypeNames(named) : [];
    }

    private static IEnumerable<ImmutableArray<string>> NamedTypeNames(INamedTypeSymbol named)
    {
        ImmutableArray<string> words = TypeWords(named);
        if (named.TypeArguments is [{ SpecialType: SpecialType.None } argument] && TypeWords(argument) is { IsEmpty: false } subject)
        {
            foreach (ImmutableArray<string> qualifier in Suffixes(subject))
            {
                yield return [.. qualifier, .. words];
            }
        }
        foreach (ImmutableArray<string> name in Suffixes(words))
        {
            yield return name;
        }
    }

    private static IEnumerable<ImmutableArray<string>> CollectionNames(ITypeSymbol element, string? role, ImmutableArray<string> fromValue)
    {
        ImmutableArray<string> singular = ElementWords(element);
        if (singular.IsEmpty)
        {
            yield break;
        }
        string plural = singular[^1].Pluralize(inputIsKnownToBeSingular: false);
        foreach (ImmutableArray<string> name in Suffixes(singular.SetItem(singular.Length - 1, plural)))
        {
            yield return name;
        }
        if (role is not null)
        {
            if (fromValue.Length > 1 && fromValue[^1].Equals(plural, StringComparison.OrdinalIgnoreCase))
            {
                yield return [.. fromValue[..^1], singular[^1], role];
            }
            foreach (ImmutableArray<string> name in Suffixes(singular))
            {
                yield return [.. name, role];
            }
        }
    }

    private static IEnumerable<ImmutableArray<string>> DictionaryNames(INamedTypeSymbol dictionary)
    {
        ImmutableArray<string> values = ElementWords(dictionary.TypeArguments[1]);
        if (values.IsEmpty)
        {
            yield break;
        }
        ImmutableArray<string> plurals = values.SetItem(values.Length - 1, values[^1].Pluralize(inputIsKnownToBeSingular: false));
        ITypeSymbol key = dictionary.TypeArguments[0];
        ImmutableArray<string> keyWords = key.SpecialType == SpecialType.None ? TypeWords(key) : [];
        foreach (ImmutableArray<string> name in Suffixes(keyWords.IsEmpty ? ["Key"] : keyWords))
        {
            yield return [.. plurals, "By", .. name];
        }
        foreach (ImmutableArray<string> name in Suffixes(values))
        {
            yield return [.. name, "Map"];
        }
        foreach (ImmutableArray<string> name in Suffixes(plurals))
        {
            yield return name;
        }
    }

    private static string? CollectionRole(INamedTypeSymbol named, ImmutableArray<INamedTypeSymbol> shapes)
    {
        if (named.ContainingNamespace.ToDisplayString() is "System.Collections.Generic" or "System.Collections.Concurrent")
        {
            switch (named.Name)
            {
                case "Queue" or "ConcurrentQueue":
                    return "Queue";
                case "Stack" or "ConcurrentStack":
                    return "Stack";
            }
        }
        foreach (INamedTypeSymbol shape in shapes)
        {
            if (shape.Arity == 1 && IsGenericCollection(shape))
            {
                switch (shape.Name)
                {
                    case "IList" or "IReadOnlyList":
                        return "List";
                    case "ISet" or "IReadOnlySet":
                        return "Set";
                }
            }
        }
        return named.Name is "ICollection" or "IReadOnlyCollection" && IsGenericCollection(named) ? "Collection" : null;
    }

    private static bool IsGenericCollection(INamedTypeSymbol type) => type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic";

    private static ImmutableArray<string> ElementWords(ITypeSymbol element)
    {
        element = WithoutNullable(element);
        return element.SpecialType switch
        {
            SpecialType.System_Boolean => ["Flag"],
            SpecialType.System_Char => ["Character"],
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal => ["Number"],
            _ => TypeWords(element),
        };
    }

    private static ITypeSymbol WithoutNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

    private static IEnumerable<ImmutableArray<string>> Suffixes(ImmutableArray<string> words)
    {
        for (int i = 0; i < words.Length; i++)
        {
            yield return words[i..];
        }
    }

    private static ImmutableArray<string> TypeWords(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol or IArrayTypeSymbol or IPointerTypeSymbol || type.TypeKind == TypeKind.Error)
        {
            return [];
        }
        string name = type.Name;
        return Words(type.TypeKind == TypeKind.Interface && name is ['I', _, ..] && char.IsUpper(name[1]) ? name[1..] : name);
    }

    private static ImmutableArray<string> Words(string name)
    {
        using TemporaryArray<TextSpan> parts = TemporaryArray<TextSpan>.Empty;
        StringBreaker.AddWordParts(name, ref parts.AsRef());
        ImmutableArray<string>.Builder words = ImmutableArray.CreateBuilder<string>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            TextSpan part = parts[i];
            if (name[part.Start] != '_')
            {
                words.Add(name[part.Start..part.End]);
            }
        }
        return words.ToImmutable();
    }
}
