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

namespace Passenger.Roslyn.Extension;

/// <summary>Finds SQL strings with Roslyn's embedded-language rules and maps their decoded characters to C# source.</summary>
internal static class SqlStrings
{
    private static readonly ImmutableArray<string> Names = ["Sql"];
    private static readonly EmbeddedLanguageCommentDetector Comments = new(Names);

    public static async Task<EmbeddedSql[]?> InAsync(Document document, TextSpan range, CancellationToken cancel)
    {
        document = document.WithFrozenPartialSemantics(cancel);
        SyntaxNode root = await document.GetRequiredSyntaxRootAsync(cancel).ConfigureAwait(false);
        EmbeddedLanguageInfo info = document.GetRequiredLanguageService<IEmbeddedLanguagesProvider>().EmbeddedLanguageInfo;
        EmbeddedLanguageDetector detector = new(info, Names, Comments);
        SemanticModel? model = null;
        List<EmbeddedSql> strings = [];
        foreach (SyntaxNode node in root.DescendantNodes(range))
        {
            cancel.ThrowIfCancellationRequested();
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
            if (IsSql(anchor, info, detector, model, cancel) && Read(anchor, info.VirtualCharService, cancel) is { } embedded)
            {
                strings.Add(embedded);
            }
        }
        return strings.Count == 0 ? null : [.. strings];
    }

    private static bool IsSql(SyntaxToken anchor, EmbeddedLanguageInfo info, EmbeddedLanguageDetector detector,
        SemanticModel model, CancellationToken cancel)
    {
        if (anchor.Parent is not InterpolatedStringExpressionSyntax)
        {
            if (!detector.IsEmbeddedLanguageToken(anchor, model, cancel, out _, out IEnumerable<string>? commentOptions))
            {
                return false;
            }
            if (commentOptions is null)
            {
                return true;
            }
        }
        return HasOwnComment(anchor, info, detector)
            || (detector.IsEmbeddedLanguageStringLiteralToken_Direct(anchor, model, cancel, out string? name)
                && detector.IsEmbeddedLanguageIdentifier(name));
    }

    /// <summary>An outer SQL marker must not pass through an interpolation hole into its C# expressions.</summary>
    private static bool HasOwnComment(SyntaxToken anchor, EmbeddedLanguageInfo info, EmbeddedLanguageDetector detector)
    {
        if (Matches(anchor.GetPreviousToken().TrailingTrivia))
        {
            return true;
        }
        for (SyntaxNode? node = anchor.Parent; node is not null and not InterpolationSyntax; node = node.Parent)
        {
            if (Matches(node.GetLeadingTrivia()))
            {
                return true;
            }
            if (node is StatementSyntax)
            {
                break;
            }
        }
        return false;

        bool Matches(SyntaxTriviaList trivia) => detector.HasLanguageComment(trivia, info.SyntaxFacts, out _, out _);
    }

    private static EmbeddedSql? Read(SyntaxToken anchor, IVirtualCharService characters, CancellationToken cancel)
    {
        StringBuilder text = new();
        List<EmbeddedSqlPart> parts = [];
        IEnumerable<SyntaxToken?> pieces = anchor.Parent is InterpolatedStringExpressionSyntax interpolation
            ? interpolation.Contents.Select(static piece => (piece as InterpolatedStringTextSyntax)?.TextToken)
            : [anchor];
        foreach (SyntaxToken? piece in pieces)
        {
            if (piece is not { } token)
            {
                // A placeholder preserves the SQL state across a hole without giving it a C# range.
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
                    parts.Add(new EmbeddedSqlPart(text.Length, span.Start, 1, span.Length));
                }
                text.Append(character.Value);
            }
        }
        return parts.Count == 0 ? null : new EmbeddedSql(text.ToString(), [.. parts]);
    }
}
