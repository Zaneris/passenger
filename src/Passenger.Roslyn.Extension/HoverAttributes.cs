using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Passenger.Roslyn.Extension;

/// <summary>Adds the bound C# symbol's attributes to its displayed Quick Info signature.</summary>
internal static class HoverAttributes
{
    private static readonly TaggedText LineBreak = new(TextTags.LineBreak, Environment.NewLine);
    private static readonly TaggedText Space = new(TextTags.Space, " ");
    private static readonly SymbolDisplayFormat AttributeType = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

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
        ImmutableArray<AttributeData> attributes = declaration.GetAttributes();
        ImmutableArray<IParameterSymbol> parameters = symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            INamedTypeSymbol { DelegateInvokeMethod: { } invoke } => invoke.Parameters,
            _ => [],
        };
        ImmutableArray<TaggedText> parts = section.TaggedParts;
        int copied = block + 1;
        int[] starts = parameters.Any(p => !p.GetAttributes().IsEmpty)
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
            AddAttributes(tags, generator, parameters[i].GetAttributes(), Space, cancel);
            copied = start;
        }
        tags.AddRange(parts.AsSpan()[copied..]);
        return quickInfo.Sections.Replace(section, QuickInfoSection.Create(section.Kind, tags.ToImmutable()));
    }

    // Match Roslyn's tagged parameter names in the outer list. Tuple fields and optional brackets can occur inside it.
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
                tags.Add(new TaggedText(TextTags.Text, list.NormalizeWhitespace(eol: separator.Text, elasticTrivia: false).ToString()));
            }
            tags.Add(new TaggedText(TextTags.Punctuation, "]"));
            tags.Add(separator);
        }
    }

    // Keep the element type so empty and mixed arrays remain valid C#.
    private static ExpressionSyntax AttributeValue(SyntaxGenerator generator, TypedConstant value) =>
        value is { Kind: TypedConstantKind.Array, IsNull: false, Type: IArrayTypeSymbol type }
            ? SyntaxFactory.ArrayCreationExpression(
                (ArrayTypeSyntax)SyntaxFactory.ParseTypeName(type.ToDisplayString(AttributeType)),
                SyntaxFactory.InitializerExpression(SyntaxKind.ArrayInitializerExpression,
                    SyntaxFactory.SeparatedList(value.Values.Select(item => AttributeValue(generator, item)))))
            : (ExpressionSyntax)generator.TypedConstantExpression(value);

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
