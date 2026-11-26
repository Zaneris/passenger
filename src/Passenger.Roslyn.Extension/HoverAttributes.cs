using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Passenger.Roslyn.Extension;

/// <summary>A C# card's signature with the attributes declared on the symbol it shows, written as C# writes them, their
/// literals coloured as code's: the symbol's own each on a line above it, a parameter's before that parameter. An attribute
/// whose type, or a type an argument of it names, is out of reach where the card is asked (a library's own, a compiler's
/// state machine) is left out, as no code there could write it.</summary>
internal static class HoverAttributes
{
    private static readonly TaggedText LineBreak = new(TextTags.LineBreak, Environment.NewLine);
    private static readonly TaggedText Space = new(TextTags.Space, " ");
    private static readonly SymbolDisplayFormat AttributeType = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>The quick info's sections, its description's signature with the attributes of the symbol at its place; as
    /// given where there are none to show.</summary>
    public static async Task<ImmutableArray<QuickInfoSection>> AnnotateAsync(Document document, QuickInfoItem quickInfo, CancellationToken cancel)
    {
        QuickInfoSection? section = quickInfo.Sections.FirstOrDefault(s => s.Kind == QuickInfoSectionKinds.Description);
        int block = section?.TaggedParts.IndexOf(new TaggedText(TextTags.CodeBlockStart, string.Empty)) ?? -1;
        if (section is null || block < 0)
        {
            return quickInfo.Sections;
        }

        int position = quickInfo.Span.Start;
        SemanticModel model = await document.GetRequiredSemanticModelAsync(cancel).ConfigureAwait(false);
        TokenSemanticInfo info = await SymbolFinder.GetSemanticInfoAtPositionAsync(model, position, document.Project.Solution.Services, cancel)
            .ConfigureAwait(false);
        ISymbol? symbol = info.AliasSymbol?.Target ?? (info.ReferencedSymbols is [var referenced] ? referenced : null);
        if (symbol is not (INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol))
        {
            return quickInfo.Sections;
        }

        ISymbol declaration = (symbol as IMethodSymbol)?.ReducedFrom ?? symbol;
        // An explicit constructor name is a use of the type, but the signature shows the constructor's parameters.
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }
            && model.SyntaxTree.GetRoot(cancel).FindToken(position, findInsideTrivia: true).Parent is SimpleNameSyntax name
            && model.GetSymbolInfo(name, cancel).Symbol is INamedTypeSymbol type)
        {
            declaration = type;
        }
        ImmutableArray<AttributeData> attributes = Reachable(declaration.GetAttributes());
        ImmutableArray<IParameterSymbol> parameters = ((symbol as INamedTypeSymbol)?.DelegateInvokeMethod ?? symbol).GetParameters();
        ImmutableArray<TaggedText> parts = section.TaggedParts;
        int copied = block + 1;
        int[] starts = parameters.Any(p => !Reachable(p.GetAttributes()).IsEmpty)
            ? ParameterStarts(parts, copied, parameters, symbol is IPropertySymbol { IsIndexer: true } ? "[" : "(") : [];
        if (attributes.IsEmpty && starts.Length == 0)
        {
            return quickInfo.Sections;
        }

        SyntaxGenerator generator = SyntaxGenerator.GetGenerator(document);
        ImmutableArray<TaggedText>.Builder tags = ImmutableArray.CreateBuilder<TaggedText>();
        tags.AddRange(parts.AsSpan(0, copied));
        AddAttributes(tags, generator, attributes, LineBreak, cancel);
        for (int i = 0; i < starts.Length; i++)
        {
            int start = starts[i];
            tags.AddRange(parts.AsSpan(copied, start - copied));
            AddAttributes(tags, generator, Reachable(parameters[i].GetAttributes()), Space, cancel);
            copied = start;
        }
        tags.AddRange(parts.AsSpan()[copied..]);
        return quickInfo.Sections.Replace(section, QuickInfoSection.Create(section.Kind, tags.ToImmutable()));

        ImmutableArray<AttributeData> Reachable(ImmutableArray<AttributeData> all) => [.. all.Where(attribute =>
            attribute.AttributeClass is { } attributeClass && model.IsAccessible(position, attributeClass)
            && attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(argument => argument.Value)).All(Named))];

        bool Named(TypedConstant value) => value.Kind switch
        {
            TypedConstantKind.Type => value.Value is not ITypeSymbol typed || model.IsAccessible(position, typed),
            TypedConstantKind.Array => value.IsNull || value.Values.All(Named),
            _ => true,
        };
    }

    /// <summary>Where each parameter begins among the signature's parts, found by its name in the outermost list opened
    /// by <paramref name="opening"/> (a tuple's names and an optional parameter's brackets lie inside it); none where they
    /// are not all found.</summary>
    private static int[] ParameterStarts(ImmutableArray<TaggedText> parts, int start,
        ImmutableArray<IParameterSymbol> parameters, string opening)
    {
        int[] starts = new int[parameters.Length];
        int matched = 0;
        int depth = 0;
        int from = -1;
        for (int i = start; i < parts.Length && parts[i].Tag != TextTags.CodeBlockEnd; i++)
        {
            switch ((parts[i].Tag, parts[i].Text))
            {
                case (TextTags.Punctuation, "(" or "[" or "<" or "{"):
                    if (depth++ == 0)
                    {
                        from = parts[i].Text == opening ? i + 1 : -1;
                        matched = 0;
                    }
                    break;
                case (TextTags.Punctuation, ")" or "]" or ">" or "}") when depth > 0:
                    if (--depth == 0)
                    {
                        if (matched == starts.Length)
                        {
                            return starts;
                        }
                        from = -1;
                    }
                    break;
                case (TextTags.Punctuation, ",") when depth == 1 && from >= 0:
                    from = i + 1;
                    break;
                case (TextTags.Space, _) when from == i:
                    from++;
                    break;
                case (TextTags.Parameter, var name) when from >= 0 && matched < starts.Length
                    && name.AsSpan().TrimStart('@').SequenceEqual(parameters[matched].Name):
                    starts[matched++] = from;
                    break;
            }
        }
        return [];
    }

    /// <summary>Each attribute as C# writes it, its type's name without its suffix, then <paramref name="separator"/>.</summary>
    private static void AddAttributes(ImmutableArray<TaggedText>.Builder tags, SyntaxGenerator generator,
        ImmutableArray<AttributeData> attributes, TaggedText separator, CancellationToken cancel)
    {
        foreach (AttributeData attribute in attributes)
        {
            cancel.ThrowIfCancellationRequested();
            if (attribute is not { AttributeClass: { TypeKind: not TypeKind.Error } type, AttributeConstructor: not null })
            {
                continue;
            }
            tags.Add(new TaggedText(TextTags.Punctuation, "["));
            tags.Add(new TaggedText(TextTags.Class, AttributeName(type)));
            if (!attribute.ConstructorArguments.IsEmpty || !attribute.NamedArguments.IsEmpty)
            {
                IEnumerable<AttributeArgumentSyntax> arguments = attribute.ConstructorArguments
                    .Select(value => SyntaxFactory.AttributeArgument(AttributeValue(generator, value)))
                    .Concat(attribute.NamedArguments.Select(pair =>
                        (AttributeArgumentSyntax)generator.AttributeArgument(pair.Key, AttributeValue(generator, pair.Value))));
                AttributeArgumentListSyntax list = SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(arguments));
                AddArgumentTags(tags, list.NormalizeWhitespace(eol: separator.Text, elasticTrivia: false));
            }
            tags.Add(new TaggedText(TextTags.Punctuation, "]"));
            tags.Add(separator);
        }
    }

    /// <summary>An attribute's arguments, its literals and keywords tagged as code's, the rest as text.</summary>
    private static void AddArgumentTags(ImmutableArray<TaggedText>.Builder tags, AttributeArgumentListSyntax arguments)
    {
        string text = arguments.ToString();
        int copied = 0;
        foreach (SyntaxToken token in arguments.DescendantTokens())
        {
            string? kind = token.Kind() switch
            {
                SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken => TextTags.StringLiteral,
                SyntaxKind.NumericLiteralToken => TextTags.NumericLiteral,
                var keyword when SyntaxFacts.IsKeywordKind(keyword) => TextTags.Keyword,
                _ => null,
            };
            if (kind is null)
            {
                continue;
            }
            int start = token.SpanStart - arguments.SpanStart;
            tags.Add(new TaggedText(TextTags.Text, text[copied..start]));
            tags.Add(new TaggedText(kind, token.Text));
            copied = start + token.Span.Length;
        }
        tags.Add(new TaggedText(TextTags.Text, text[copied..]));
    }

    /// <summary>An argument's value as C#, an array's with its element type, which an empty or mixed one needs.</summary>
    private static ExpressionSyntax AttributeValue(SyntaxGenerator generator, TypedConstant value) =>
        value is { Kind: TypedConstantKind.Array, IsNull: false, Type: IArrayTypeSymbol type }
            ? SyntaxFactory.ArrayCreationExpression(
                (ArrayTypeSyntax)SyntaxFactory.ParseTypeName(type.ToDisplayString(AttributeType)),
                SyntaxFactory.InitializerExpression(SyntaxKind.ArrayInitializerExpression,
                    SyntaxFactory.SeparatedList(value.Values.Select(item => AttributeValue(generator, item)))))
            : (ExpressionSyntax)generator.TypedConstantExpression(value);

    /// <summary>An attribute type's name as it is written in brackets: without "Attribute" where the rest is no keyword.</summary>
    private static string AttributeName(INamedTypeSymbol type)
    {
        const string suffix = "Attribute";
        return string.Concat(type.ToDisplayParts(AttributeType).Select(part =>
            SymbolEqualityComparer.Default.Equals(part.Symbol, type) && part.ToString() is { } name
            && name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)
            && SyntaxFacts.GetKeywordKind(name[..^suffix.Length]) == SyntaxKind.None
                ? name[..^suffix.Length] : part.ToString()));
    }
}
