using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Where the method a debugger's frame stopped in is written in a C# file, read from its parse: the member holding
/// the place it stopped at, with its lambdas and local functions; an accessor apart from its property's others; an
/// expression-bodied property's body or a property's initializer alone; a field's declaration, its initializer; a primary
/// constructor's parameters and base call; and every top-level statement of the file, which are one method. Only there
/// does a name mean what an evaluation in that frame reads.</summary>
public static class MethodBounds
{
    /// <summary>The method holding a place (line and column from zero, the editor's) of the file's text; null where the place
    /// is in no code that runs (a type's own words, a namespace's) or past the text's end.</summary>
    public static TextRange? At(ParsedText parsed, int line, int column)
    {
        if (line < 0 || line >= parsed.Text.LineCount)
        {
            return null;
        }
        LineSpan held = parsed.Text.GetLine(line);
        SyntaxNode root = parsed.Root;
        SyntaxToken token = root.FindToken(Math.Min(held.Start + Math.Clamp(column, 0, held.Length), Math.Max(0, root.FullSpan.End - 1)));
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case AccessorDeclarationSyntax or BaseMethodDeclarationSyntax or BaseFieldDeclarationSyntax
                    or ArrowExpressionClauseSyntax { Parent: BasePropertyDeclarationSyntax } or EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax }:
                    return Of(node.Span);
                case GlobalStatementSyntax global:
                    return TopLevel(global);
                case TypeDeclarationSyntax { ParameterList: { } parameters } type:
                    TextSpan header = TextSpan.FromBounds(parameters.SpanStart, type.BaseList?.Span.End ?? parameters.Span.End);
                    return header.Contains(token.SpanStart) ? Of(header) : null;
                case MemberDeclarationSyntax:
                    return null;
            }
        }
        return null;
    }

    /// <summary>From the first top-level statement beside one to the last: the file's Main.</summary>
    private static TextRange TopLevel(GlobalStatementSyntax global)
    {
        TextSpan span = global.Span;
        foreach (SyntaxNode beside in global.Parent?.ChildNodes() ?? [])
        {
            if (beside is GlobalStatementSyntax statement)
            {
                span = TextSpan.FromBounds(Math.Min(span.Start, statement.SpanStart), Math.Max(span.End, statement.Span.End));
            }
        }
        return Of(span);
    }

    private static TextRange Of(TextSpan span) => TextRange.FromBounds(span.Start, span.End);
}
