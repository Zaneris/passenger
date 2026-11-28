using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.EmbeddedLanguages;
using Microsoft.CodeAnalysis.EmbeddedLanguages.VirtualChars;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Passenger.Lsp;
using EmbeddedText = Passenger.Lsp.EmbeddedText;

namespace Passenger.Roslyn.Extension;

/// <summary>The strings of a C# file that hold a language Roslyn colours none of, found by Roslyn's own embedded-language
/// rules (a <c>[StringSyntax]</c> they go to, a <c>lang=</c> comment before them) for each language <see cref="Names"/>
/// lists, and the format an interpolation's hole gives a date or a time, each given by its language's name, its text as
/// decoded (Roslyn's virtual characters) and the source each run of it came from, for the client to colour as that
/// language's files are, or by its grammar; a regex's and JSON's come as Roslyn's own tokens.</summary>
internal static class EmbeddedTexts
{
    /// <summary>The languages a string is read as, by the names <c>[StringSyntax]</c> and <c>lang=</c> give them.</summary>
    private static readonly ImmutableArray<string> Names = ["Sql", "Xml", "Html", "Css", "JavaScript", "Yaml", "Date", "Time", "DateTime", DateTimeFormat,
        "DateOnlyFormat", "TimeOnlyFormat"];

    /// <summary>The name a date's or time's format goes by, given an interpolation's format of a value of one.</summary>
    private const string DateTimeFormat = "DateTimeFormat";

    private static readonly EmbeddedLanguageCommentDetector Comments = new(Names);

    /// <summary>The strings in a range of the document that hold one of <see cref="Names"/>' languages, and the formats of
    /// dates and times in its interpolations' holes; null for none.</summary>
    public static async Task<EmbeddedText[]?> InAsync(Document document, TextSpan range, CancellationToken cancel)
    {
        document = document.WithFrozenPartialSemantics(cancel);
        SyntaxNode root = await document.GetRequiredSyntaxRootAsync(cancel).ConfigureAwait(false);
        EmbeddedLanguageInfo info = document.GetRequiredLanguageService<IEmbeddedLanguagesProvider>().EmbeddedLanguageInfo;
        EmbeddedLanguageDetector detector = new(info, Names, Comments);
        SemanticModel? model = null;
        List<EmbeddedText> strings = [];
        foreach (SyntaxNode node in root.DescendantNodes(range))
        {
            cancel.ThrowIfCancellationRequested();
            if (node is InterpolationSyntax { FormatClause: { } clause } hole)
            {
                model ??= await document.GetRequiredSemanticModelAsync(cancel).ConfigureAwait(false);
                if (IsDateOrTime(model.GetTypeInfo(hole.Expression, cancel).Type) && Read(DateTimeFormat, clause.FormatStringToken, info.VirtualCharService, cancel) is { } format)
                {
                    strings.Add(format);
                }
                continue;
            }
            SyntaxToken anchor = node switch
            {
                LiteralExpressionSyntax literal when info.IsAnyStringLiteral(literal.Token.RawKind) => literal.Token,
                InterpolatedStringExpressionSyntax interpolation => interpolation.StringStartToken,
                _ => default,
            };
            if (anchor.RawKind == 0 || info.SyntaxFacts.WalkUpParentheses(node).Parent.IsKind(SyntaxKind.AddExpression))
            {
                continue;
            }
            model ??= await document.GetRequiredSemanticModelAsync(cancel).ConfigureAwait(false);
            if (LanguageOf(anchor, info, detector, model, cancel) is { } language && Read(language, anchor, info.VirtualCharService, cancel) is { } embedded)
            {
                strings.Add(embedded);
            }
        }
        return strings.Count == 0 ? null : [.. strings];
    }

    /// <summary>Whether a value is a date's or a time's, whose format an interpolation's hole may give after its colon.</summary>
    private static bool IsDateOrTime(ITypeSymbol? type) =>
        (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [var held] } ? held : type)
            is INamedTypeSymbol { Name: "DateTime" or "DateTimeOffset" or "DateOnly" or "TimeOnly", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } };

    /// <summary>The language a string holds by <see cref="Names"/>' name, or null: an interpolated string's only by a comment
    /// of its own or what it goes to, never by one above an expression in a hole that holds it.</summary>
    private static string? LanguageOf(SyntaxToken anchor, EmbeddedLanguageInfo info, EmbeddedLanguageDetector detector,
        SemanticModel model, CancellationToken cancel)
    {
        if (anchor.Parent is not InterpolatedStringExpressionSyntax)
        {
            if (!detector.IsEmbeddedLanguageToken(anchor, model, cancel, out string? identifier, out IEnumerable<string>? commentOptions))
            {
                return null;
            }
            if (commentOptions is null)
            {
                return identifier;
            }
        }
        return OwnComment(anchor, info, detector)
            ?? (detector.IsEmbeddedLanguageStringLiteralToken_Direct(anchor, model, cancel, out string? name) && detector.IsEmbeddedLanguageIdentifier(name)
                ? name
                : null);
    }

    /// <summary>The language a <c>lang=</c> comment of the string's own names, up to its statement; never one above a hole
    /// the string stands in, which is the outer string's.</summary>
    private static string? OwnComment(SyntaxToken anchor, EmbeddedLanguageInfo info, EmbeddedLanguageDetector detector)
    {
        if (Named(anchor.GetPreviousToken().TrailingTrivia) is { } trailing)
        {
            return trailing;
        }
        for (SyntaxNode? node = anchor.Parent; node is not null and not InterpolationSyntax; node = node.Parent)
        {
            if (Named(node.GetLeadingTrivia()) is { } leading)
            {
                return leading;
            }
            if (node is StatementSyntax)
            {
                break;
            }
        }
        return null;

        string? Named(SyntaxTriviaList trivia) => detector.HasLanguageComment(trivia, info.SyntaxFacts, out string? identifier, out _) ? identifier : null;
    }

    /// <summary>A string's text as decoded, a hole's place one '?' of no source so the language reads on past it, and the
    /// runs of the source it came from; null where it has no characters to read.</summary>
    private static EmbeddedText? Read(string language, SyntaxToken anchor, IVirtualCharService characters, CancellationToken cancel)
    {
        StringBuilder text = new();
        List<EmbeddedTextPart> parts = [];
        IEnumerable<SyntaxToken?> pieces = anchor.Parent is InterpolatedStringExpressionSyntax interpolation
            ? interpolation.Contents.Select(static piece => (piece as InterpolatedStringTextSyntax)?.TextToken)
            : [anchor];
        foreach (SyntaxToken? piece in pieces)
        {
            if (piece is not { } token)
            {
                // A hole is a character of no source, so the language reads on past it as it would past a value.
                text.Append('?');
                continue;
            }
            VirtualCharSequence chars = characters.TryConvertToVirtualChars(token);
            if (chars.IsDefault)
            {
                continue;
            }
            for (int i = 0; i < chars.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                VirtualChar character = chars[i];
                TextSpan span = character.Span;
                if (parts.Count > 0 && parts[^1] is { } last && last.End == text.Length
                    && last.Width == span.Length && last.SourceOffset(last.End) == span.Start)
                {
                    parts[^1] = last with { Length = last.Length + 1 };
                }
                else
                {
                    parts.Add(new EmbeddedTextPart(text.Length, span.Start, 1, span.Length));
                }
                text.Append(character.Value);
            }
        }
        return parts.Count == 0 ? null : new EmbeddedText(language, text.ToString(), [.. parts]);
    }
}
