using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>The lines of a C# file that begin inside a string running over lines (a verbatim or raw string, interpolated
/// or not, UTF-8 or not), whose leading whitespace is the string's own text: a raw string's closing line among them, which
/// its lines' whitespace must go on matching, and those of the branches a preprocessor condition leaves out, whose strings
/// are the program's once it is defined. By the editor's line numbers in the text the syntax was read from.</summary>
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
        InBranchesLeftOut(root, text, lines);
        return lines;
    }

    /// <summary>The strings of each run of text left out, read by the lexer alone as no parse reads them: a directive it meets
    /// leaves out a branch of its own, read in its turn, so both ways of every condition are read. A string left open by the
    /// run's end runs to it, and a token over lines can be nothing but a string.</summary>
    private static void InBranchesLeftOut(SyntaxNode root, TextSnapshot text, HashSet<int> lines)
    {
        CSharpParseOptions? options = root.SyntaxTree.Options as CSharpParseOptions;
        Stack<TextSpan> unread = new(LeftOut(root.DescendantTrivia()));
        while (unread.TryPop(out TextSpan run))
        {
            foreach (SyntaxToken token in DeepText.Read(run.Length, () => SyntaxFactory.ParseTokens(text.GetText(run.Start, run.Length), initialTokenPosition: run.Start, options: options).ToList()))
            {
                Inside(token.Span, text, lines);
                foreach (TextSpan deeper in LeftOut(token.LeadingTrivia))
                {
                    unread.Push(deeper);
                }
            }
        }
    }

    /// <summary>Each run of text a condition left out, the directives of conditions inside it with it: a line like one inside a
    /// string left out is taken for one, and a run read whole reads it as the string's.</summary>
    private static IEnumerable<TextSpan> LeftOut(IEnumerable<SyntaxTrivia> trivia)
    {
        int start = -1;
        int end = -1;
        foreach (SyntaxTrivia part in trivia)
        {
            if (part.IsKind(SyntaxKind.DisabledTextTrivia) || part.GetStructure() is DirectiveTriviaSyntax { IsActive: false })
            {
                start = start < 0 ? part.FullSpan.Start : start;
                end = part.FullSpan.End;
            }
            else if (part.IsDirective && start >= 0)
            {
                yield return TextSpan.FromBounds(start, end);
                start = -1;
            }
        }
        if (start >= 0)
        {
            yield return TextSpan.FromBounds(start, end);
        }
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
