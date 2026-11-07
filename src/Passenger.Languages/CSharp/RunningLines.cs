using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Whether code may run on a line of a C# file, read from its parse, where the debugger binds a line breakpoint:
/// a line of only blanks, comments and directives, usings, a namespace's or a type's own words, attributes, a parameter
/// list, an accessor's keyword or a member with nothing to run (no body, no initializer) runs none, and a breakpoint there
/// moves to the next line that may. Where it can't be told (a constructor's declaration, which runs its base call, a
/// primary constructor's type before its body), a line runs.</summary>
public static class RunningLines
{
    /// <summary>Whether code may run on a line (from zero) of the file's text.</summary>
    public static bool Runs(ParsedText parsed, int line)
    {
        if (line < 0 || line >= parsed.Text.LineCount)
        {
            return false;
        }
        LineSpan span = parsed.Text.GetLine(line);
        // At least a character, so a blank line inside a string over several lines finds the string.
        int end = Math.Max(span.Start + span.Length, span.Start + 1);
        foreach (SyntaxToken token in parsed.Root.DescendantTokens(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(span.Start, end)))
        {
            if (token.Span.Length > 0 && token.Span.Start < end && token.Span.End > span.Start && Runs(token))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The first line after a line (both from zero) where code may run, or null where none does to the file's end,
    /// found in one walk of the tokens after it, so a long class costs the window's thread its tokens once.</summary>
    public static int? NextAfter(ParsedText parsed, int line)
    {
        int next = Math.Max(line + 1, 0);
        if (next >= parsed.Text.LineCount)
        {
            return null;
        }
        int from = parsed.Text.GetLineStart(next);
        foreach (SyntaxToken token in parsed.Root.DescendantTokens(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(from, parsed.Text.Length)))
        {
            if (token.Span.Length > 0 && token.Span.End > from && Runs(token))
            {
                return parsed.Text.GetLineFromOffset(Math.Max(token.SpanStart, from));
            }
        }
        return null;
    }

    /// <summary>Whether a token is in code that runs, by the nearest of its ancestors that says.</summary>
    private static bool Runs(SyntaxToken token)
    {
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case AttributeListSyntax or UsingDirectiveSyntax or ExternAliasDirectiveSyntax or TypeParameterListSyntax or TypeParameterConstraintClauseSyntax:
                    return false;
                case ParameterListSyntax parameters:
                    return parameters.Parent is ConstructorDeclarationSyntax or TypeDeclarationSyntax || !IsMemberHeader(parameters.Parent);
                case BracketedParameterListSyntax:
                    return false;
                case StatementSyntax or ArrowExpressionClauseSyntax or ConstructorInitializerSyntax or GlobalStatementSyntax:
                    return true;
                case EqualsValueClauseSyntax value:
                    return value.Parent switch
                    {
                        ParameterSyntax or EnumMemberDeclarationSyntax => false,
                        VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax field } => !field.Modifiers.Any(SyntaxKind.ConstKeyword),
                        _ => true,
                    };
                case AccessorDeclarationSyntax or AccessorListSyntax or EnumMemberDeclarationSyntax or DelegateDeclarationSyntax or BaseNamespaceDeclarationSyntax
                    or CompilationUnitSyntax:
                    return false;
                case ConstructorDeclarationSyntax:
                    return true;
                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax:
                    return false;
                case BaseTypeDeclarationSyntax type:
                    return type is TypeDeclarationSyntax { ParameterList: not null } primary && token.SpanStart < primary.OpenBraceToken.SpanStart;
            }
        }
        return true;
    }

    /// <summary>Whether a parameter list's owner is a member whose declaration runs nothing of its own: a method, an operator,
    /// a delegate or a local function's, never a lambda's or an anonymous method's.</summary>
    private static bool IsMemberHeader(SyntaxNode? owner) =>
        owner is BaseMethodDeclarationSyntax or DelegateDeclarationSyntax or LocalFunctionStatementSyntax;
}
