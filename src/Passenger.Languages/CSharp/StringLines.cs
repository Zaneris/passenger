using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>The lines of a C# file that begin inside a string running over lines (a verbatim or raw string, interpolated
/// or not, UTF-8 or not), whose leading whitespace is the string's own text: a raw string's closing line among them, which
/// its lines' whitespace must go on matching. By the editor's line numbers in the text the syntax was read from.</summary>
public static class StringLines
{
    public static IReadOnlySet<int> Of(SyntaxNode root, TextSnapshot text)
    {
        HashSet<int> lines = [];
        foreach (SyntaxNodeOrToken part in root.DescendantNodesAndTokens())
        {
            bool spans = part.IsToken
                ? part.AsToken() is var token && (token.Kind() is SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken
                    || (token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.Utf8StringLiteralToken && token.Text.StartsWith('@')))
                : part.AsNode() is InterpolatedStringExpressionSyntax interpolated
                    && interpolated.StringStartToken.Kind() is SyntaxKind.InterpolatedVerbatimStringStartToken or SyntaxKind.InterpolatedMultiLineRawStringStartToken;
            if (spans)
            {
                Inside(part.Span, text, lines);
            }
        }
        return lines;
    }

    /// <summary>The lines starting after a span's start and before its end.</summary>
    private static void Inside(TextSpan span, TextSnapshot text, HashSet<int> lines)
    {
        for (int line = text.GetLineFromOffset(span.Start) + 1; line < text.LineCount && text.GetLineStart(line) < span.End; line++)
        {
            lines.Add(line);
        }
    }
}
