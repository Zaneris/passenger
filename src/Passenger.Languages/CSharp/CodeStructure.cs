using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Where the moves by a C# file's structure go, from its one parse: Method Up and Down from member to member, a
/// containing declaration gone to or selected, a code block's braces, and the file's regions. A member stands where its
/// declaration begins past its attributes, a declaration is gone to at its name, and a block is any pair of braces of
/// the code (a body's, a type's, an initializer's).</summary>
public static class CodeStructure
{
    /// <summary>Where the member before or after <paramref name="caret"/> begins: a type, a method, a property, a field, an
    /// enum's member or a local function; null where none is.</summary>
    public static int? Member(ParsedText csharp, int caret, bool down)
    {
        int? found = null;
        // A member stands among members and statements, never inside an expression.
        foreach (SyntaxNode node in csharp.Root.DescendantNodes(n => n is not ExpressionSyntax))
        {
            if (StartOf(node) is not { } start || (down ? start <= caret : start >= caret))
            {
                continue;
            }
            if (found is not { } best || (down ? start < best : start > best))
            {
                found = start;
            }
        }
        return found;
    }

    /// <summary>The declaration around a selection, innermost first, that is not the selection already: to go to, its
    /// name, past a caret standing at the name of the one inside it; to select, its whole text but its doc comment.</summary>
    public static (int Name, TextRange Whole)? Containing(ParsedText csharp, TextRange selection, bool select)
    {
        SyntaxNode root = csharp.Root;
        SyntaxToken token = root.FindToken(Math.Min(selection.Start, Math.Max(0, root.FullSpan.End - 1)));
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            if (StartOf(node) is null || NameOf(node) is not { } name)
            {
                continue;
            }
            TextRange whole = TextRange.FromBounds(node.SpanStart, node.Span.End);
            bool around = whole.Start <= selection.Start && whole.End >= selection.End;
            if (around && (select ? whole.Length > selection.Length : !(selection.Length == 0 && selection.Start == name.SpanStart)))
            {
                return (name.SpanStart, whole);
            }
        }
        return null;
    }

    /// <summary>The braces of the innermost block around <paramref name="caret"/>, the opening brace's offset and the
    /// closing's end; one whose brace the caret stands at already (before the opening, after the closing) gives the block
    /// around it, so the move goes on outward.</summary>
    public static TextRange? Block(ParsedText csharp, int caret, bool end)
    {
        SyntaxNode root = csharp.Root;
        SyntaxToken token = root.FindToken(Math.Min(caret, Math.Max(0, root.FullSpan.End - 1)));
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            SyntaxToken open = node.ChildTokens().FirstOrDefault(t => t.IsKind(SyntaxKind.OpenBraceToken));
            SyntaxToken close = node.ChildTokens().LastOrDefault(t => t.IsKind(SyntaxKind.CloseBraceToken));
            if (open.IsMissing || close.IsMissing || open.RawKind == 0 || close.RawKind == 0)
            {
                continue;
            }
            TextRange braces = TextRange.FromBounds(open.SpanStart, close.Span.End);
            bool inside = braces.Start < caret && caret < braces.End;
            bool there = end ? caret == braces.End : caret == braces.Start;
            if (inside || (braces.Start <= caret && caret <= braces.End && !there))
            {
                return braces;
            }
        }
        return null;
    }

    /// <summary>Each #region of the file, by its name (the text after the directive, else "region"), where its line begins.</summary>
    public static IReadOnlyList<(string Name, int Start)> Regions(ParsedText csharp) =>
        [.. csharp.Root.DescendantTrivia(descendIntoTrivia: true)
            .Where(t => t.IsKind(SyntaxKind.RegionDirectiveTrivia))
            .Select(t => (Named((RegionDirectiveTriviaSyntax)t.GetStructure()!), t.SpanStart))];

    private static string Named(RegionDirectiveTriviaSyntax region) =>
        region.EndOfDirectiveToken.LeadingTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.PreprocessingMessageTrivia)).ToString().Trim() is { Length: > 0 } name
            ? name
            : "region";

    /// <summary>Where a member a move stops at begins, past its attributes; null for a node that is no such member.</summary>
    private static int? StartOf(SyntaxNode node) => node switch
    {
        BaseNamespaceDeclarationSyntax or GlobalStatementSyntax or IncompleteMemberSyntax => null,
        MemberDeclarationSyntax member => member.AttributeLists.Count > 0 ? member.AttributeLists.Last().GetLastToken().GetNextToken().SpanStart : member.SpanStart,
        LocalFunctionStatementSyntax local => local.AttributeLists.Count > 0 ? local.AttributeLists.Last().GetLastToken().GetNextToken().SpanStart : local.SpanStart,
        _ => null,
    };

    /// <summary>A declaration's name, where it has one to go to.</summary>
    private static SyntaxToken? NameOf(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier,
        MethodDeclarationSyntax method => method.Identifier,
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        DestructorDeclarationSyntax destructor => destructor.Identifier,
        OperatorDeclarationSyntax @operator => @operator.OperatorToken,
        ConversionOperatorDeclarationSyntax conversion => conversion.Type.GetFirstToken(),
        PropertyDeclarationSyntax property => property.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
        BaseFieldDeclarationSyntax field when field.Declaration.Variables.Count > 0 => field.Declaration.Variables[0].Identifier,
        EnumMemberDeclarationSyntax enumMember => enumMember.Identifier,
        LocalFunctionStatementSyntax local => local.Identifier,
        _ => null,
    };
}
