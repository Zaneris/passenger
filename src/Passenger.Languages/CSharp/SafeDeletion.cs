using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>What Safe Delete takes out for a declaration: its text (with its lines when nothing else is on them, its doc
/// comment, and a blank line it would leave doubled), and its name and kind ("method", "field") to say what it was; or,
/// with <see cref="WhyNot"/>, why it cannot take it out alone.</summary>
public sealed record Removal(TextRange Range, string Name, string Kind, string? WhyNot = null);

/// <summary>Reads a declaration from a file's syntax for Safe Delete: what deleting it takes out.</summary>
public static class SafeDeletion
{
    /// <summary>A file not open in the editor, parsed once for what is asked of it with its project's symbols
    /// (<see cref="CSharpParsing"/>).</summary>
    public static ParsedText Parse(string text, CSharpParseOptions options) => new(CSharpSyntaxTree.ParseText(text, options), TextSnapshot.From(text));

    /// <summary>What deleting the declaration whose name is at an offset takes out of its file, or null when no name of a
    /// declaration is there.</summary>
    public static Removal? At(ParsedText syntax, int offset)
    {
        SyntaxToken name = syntax.Root.FindToken(offset);
        if (!name.IsKind(SyntaxKind.IdentifierToken) || name.Parent is not { } declared)
        {
            return null;
        }
        string text = name.ValueText;
        return declared switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax field } list } v
                => Declarator(syntax.Text, v, list, field, field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)) ? "constant" : "field"),
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: EventFieldDeclarationSyntax field } list } v => Declarator(syntax.Text, v, list, field, "event"),
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax local } list } v
                => Declarator(syntax.Text, v, list, local, "local variable"),
            MethodDeclarationSyntax m when m.Identifier == name => Whole(syntax.Text, m, text, "method"),
            PropertyDeclarationSyntax p when p.Identifier == name => Whole(syntax.Text, p, text, "property"),
            EventDeclarationSyntax e when e.Identifier == name => Whole(syntax.Text, e, text, "event"),
            ConstructorDeclarationSyntax c when c.Identifier == name => Whole(syntax.Text, c, text, "constructor"),
            EnumMemberDeclarationSyntax e => EnumMember(syntax.Text, e, text),
            DelegateDeclarationSyntax d when d.Identifier == name => Whole(syntax.Text, d, text, "delegate"),
            BaseTypeDeclarationSyntax t when t.Identifier == name => Whole(syntax.Text, t, text, t switch
            {
                InterfaceDeclarationSyntax => "interface",
                StructDeclarationSyntax => "struct",
                TypeDeclarationSyntax { Keyword.ValueText: "union" } => "union",
                RecordDeclarationSyntax => "record",
                EnumDeclarationSyntax => "enum",
                _ => "class",
            }),
            LocalFunctionStatementSyntax f when f.Identifier == name => Whole(syntax.Text, f, text, "local function"),
            ParameterSyntax => new Removal(default, text, "parameter", "Safe Delete removes a type, a member or a local; a parameter is left to Change Signature"),
            _ => null,
        };
    }

    /// <summary>One of a declaration's names: the whole declaration when it is the only one, else the name and the comma
    /// that parts it from the next (or the one before, for the last).</summary>
    private static Removal Declarator(TextSnapshot text, VariableDeclaratorSyntax variable, VariableDeclarationSyntax list, SyntaxNode declaration, string kind)
    {
        string name = variable.Identifier.ValueText;
        if (list.Variables.Count == 1)
        {
            return Whole(text, declaration, name, kind);
        }
        int index = list.Variables.IndexOf(variable);
        SeparatedSyntaxList<VariableDeclaratorSyntax> variables = list.Variables;
        TextRange range = index < variables.Count - 1
            ? TextRange.FromBounds(variable.SpanStart, variables[index + 1].SpanStart)
            : TextRange.FromBounds(variables.GetSeparator(index - 1).SpanStart, variable.Span.End);
        return new Removal(range, name, kind);
    }

    /// <summary>An enum member with the comma that parts it from the next, as a whole declaration; the last, with none
    /// after it, from the comma before it, unless a comment follows that comma, which then stays as the enum's last.</summary>
    private static Removal EnumMember(TextSnapshot text, EnumMemberDeclarationSyntax member, string name)
    {
        const string Kind = "enum member";
        SeparatedSyntaxList<EnumMemberDeclarationSyntax> members = ((EnumDeclarationSyntax)member.Parent!).Members;
        int index = members.IndexOf(member);
        if (index < members.SeparatorCount)
        {
            return Whole(text, member, members.GetSeparator(index), name, Kind);
        }
        if (index == 0 || members.GetSeparator(index - 1).TrailingTrivia.Any(t => !t.IsKind(SyntaxKind.EndOfLineTrivia) && !t.IsKind(SyntaxKind.WhitespaceTrivia)))
        {
            return Whole(text, member, member.GetLastToken(), name, Kind);
        }
        return new Removal(TextRange.FromBounds(members.GetSeparator(index - 1).SpanStart, End(member.GetLastToken())), name, Kind);
    }

    private static Removal Whole(TextSnapshot text, SyntaxNode node, string name, string kind) => Whole(text, node, node.GetLastToken(), name, kind);

    /// <summary>A whole declaration, from its doc comment to its last token (or the comma after it) and a comment after
    /// that on its line: its lines when it has them to itself, then a blank line it would leave beside another or against
    /// a brace; else its text and the spaces after it.</summary>
    private static Removal Whole(TextSnapshot text, SyntaxNode node, SyntaxToken through, string name, string kind)
    {
        int start = node.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) is { RawKind: not 0 } doc ? doc.FullSpan.Start : node.SpanStart;
        int end = End(through);
        int firstLine = text.GetLineFromOffset(start);
        int lastLine = text.GetLineFromOffset(end);
        LineSpan first = text.GetLine(firstLine);
        LineSpan last = text.GetLine(lastLine);
        if (!Blank(text, first.Start, start) || !Blank(text, end, last.End))
        {
            int after = end;
            while (after < last.End && text[after] is ' ' or '\t')
            {
                after++;
            }
            return new Removal(TextRange.FromBounds(start, after), name, kind);
        }
        int from = first.Start;
        int to = last.EndIncludingBreak;
        LineSpan? before = firstLine > 0 ? text.GetLine(firstLine - 1) : null;
        LineSpan? next = lastLine + 1 < text.LineCount ? text.GetLine(lastLine + 1) : null;
        bool beforeBlank = before is { } b && Blank(text, b.Start, b.End);
        bool nextBlank = next is { } n && n.Length + n.BreakLength > 0 && Blank(text, n.Start, n.End);
        bool opens = before is not { } o || text.GetText(o.Start, o.Length).TrimEnd().EndsWith('{');
        bool closes = next is not { } c || text.GetText(c.Start, c.Length).TrimStart().StartsWith('}');
        if (beforeBlank && (nextBlank || closes))
        {
            from = before!.Value.Start;
        }
        else if (nextBlank && opens)
        {
            to = next!.Value.EndIncludingBreak;
        }
        return new Removal(TextRange.FromBounds(from, to), name, kind);
    }

    /// <summary>Where a token ends with a comment after it on its line.</summary>
    private static int End(SyntaxToken token)
    {
        int end = token.Span.End;
        foreach (SyntaxTrivia trailing in token.TrailingTrivia)
        {
            if (!trailing.IsKind(SyntaxKind.EndOfLineTrivia) && !trailing.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                end = trailing.Span.End;
            }
        }
        return end;
    }

    private static bool Blank(TextSnapshot text, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }
        return true;
    }
}
