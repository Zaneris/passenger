using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>What Complete Current Statement does to a text: its edits, and where the caret goes once they are made.</summary>
public sealed record CompletedStatement(IReadOnlyList<TextEdit> Edits, int Caret);

/// <summary>Complete Current Statement, read from the file's syntax (Roslyn parses what is half typed, marking what it
/// lacks as missing): the statement on the caret's line gets the brackets it leaves open and its semicolon, and the
/// caret a new line after it; a header (<c>if (x</c>, a method's signature, a type's) gets its closing parenthesis and
/// braces, and the caret their body; a statement already whole, a new line after it.</summary>
public static class StatementCompletion
{
    public static CompletedStatement? At(ParsedText syntax, int caret, IndentOptions indent, string eol)
    {
        TextSnapshot text = syntax.Text;
        SyntaxNode root = syntax.Root;
        LineSpan line = text.GetLine(text.GetLineFromOffset(Math.Clamp(caret, 0, text.Length)));
        int code = line.End - 1;
        while (code >= line.Start && char.IsWhiteSpace(text[code]))
        {
            code--;
        }
        if (code < line.Start)
        {
            return null;
        }
        SyntaxToken token = root.FindToken(code);
        // A comment that leads the next line's code belongs to that code's first token, not to this line's.
        if (token.SpanStart > code)
        {
            token = token.GetPreviousToken();
        }
        if (token.RawKind == 0 || token.SpanStart < line.Start && token.Span.End <= line.Start)
        {
            return NewLine(text, line, eol);
        }
        Completer completer = new(text, indent.Unit, eol);
        if (!token.IsKind(SyntaxKind.CloseBraceToken))
        {
            for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
            {
                if (completer.Complete(node, token) is { } done)
                {
                    return done;
                }
            }
        }
        return NewLine(text, line, eol);
    }

    /// <summary>A line opened after the caret's, indented as it is: what a statement already whole gets.</summary>
    private static CompletedStatement NewLine(TextSnapshot text, LineSpan line, string eol)
    {
        string inserted = eol + TextNavigation.IndentOf(text, line.Start);
        return new CompletedStatement([new TextEdit(line.End, 0, inserted)], line.End + inserted.Length);
    }

    private sealed class Completer(TextSnapshot text, string unit, string eol)
    {
        /// <summary>The completion of a node the caret's token is in, or null to look further out.</summary>
        public CompletedStatement? Complete(SyntaxNode node, SyntaxToken token) => node switch
        {
            BlockSyntax => null,
            IfStatementSyntax s => Control(s, s.Statement, token),
            WhileStatementSyntax s => Control(s, s.Statement, token),
            ForStatementSyntax s => Control(s, s.Statement, token),
            CommonForEachStatementSyntax s => Control(s, s.Statement, token),
            UsingStatementSyntax s => Control(s, s.Statement, token),
            LockStatementSyntax s => Control(s, s.Statement, token),
            FixedStatementSyntax s => Control(s, s.Statement, token),
            ElseClauseSyntax s => Control(s, s.Statement, token),
            DoStatementSyntax s when Before(token, s.Statement) => Control(s, s.Statement, token, s.WhileKeyword.IsMissing ? " while ();" : string.Empty),
            DoStatementSyntax s => Ended(s),
            TryStatementSyntax s => Braces(s, s.Block.OpenBraceToken, s.Block.CloseBraceToken, s.Block.Statements.FirstOrDefault(), token),
            CatchClauseSyntax s => Braces(s, s.Block.OpenBraceToken, s.Block.CloseBraceToken, s.Block.Statements.FirstOrDefault(), token),
            FinallyClauseSyntax s => Braces(s, s.Block.OpenBraceToken, s.Block.CloseBraceToken, s.Block.Statements.FirstOrDefault(), token),
            SwitchStatementSyntax s => Braces(s, s.OpenBraceToken, s.CloseBraceToken, s.Sections.FirstOrDefault(), token),
            ExpressionStatementSyntax or LocalDeclarationStatementSyntax or ReturnStatementSyntax or ThrowStatementSyntax or BreakStatementSyntax
                or ContinueStatementSyntax or GotoStatementSyntax or YieldStatementSyntax or FieldDeclarationSyntax or EventFieldDeclarationSyntax
                or UsingDirectiveSyntax or DelegateDeclarationSyntax or FileScopedNamespaceDeclarationSyntax => Ended(node),
            BaseMethodDeclarationSyntax { ExpressionBody: not null } or LocalFunctionStatementSyntax { ExpressionBody: not null }
                or PropertyDeclarationSyntax { ExpressionBody: not null } or PropertyDeclarationSyntax { Initializer: not null } => Ended(node),
            BaseMethodDeclarationSyntax m => Method(m, m.Body, NeedsNoBody(m), token),
            LocalFunctionStatementSyntax f => Method(f, f.Body, false, token),
            RecordDeclarationSyntax { ParameterList: not null, OpenBraceToken.IsMissing: true } r => Ended(r),
            BaseTypeDeclarationSyntax t => Braces(t, t.OpenBraceToken, t.CloseBraceToken, (t as TypeDeclarationSyntax)?.Members.FirstOrDefault(), token),
            NamespaceDeclarationSyntax n => Braces(n, n.OpenBraceToken, n.CloseBraceToken, n.Members.FirstOrDefault(), token),
            _ => null,
        };

        /// <summary>A statement that ends with a semicolon: the brackets it leaves open closed, its semicolon, and a new
        /// line after it (after a comment that ends its line; none when more code follows on the line).</summary>
        private CompletedStatement Ended(SyntaxNode node)
        {
            SyntaxToken last = node.GetLastToken();
            string missing = Missing(last, node, statement: true);
            int end = last.Span.End;
            LineSpan line = text.GetLine(text.GetLineFromOffset(end));
            string rest = text.GetText(end, line.End - end).Trim();
            if (rest.Length > 0 && !rest.StartsWith("//", StringComparison.Ordinal) && !rest.StartsWith("/*", StringComparison.Ordinal))
            {
                return new CompletedStatement([new TextEdit(end, 0, missing)], end + missing.Length);
            }
            string next = eol + TextNavigation.IndentOf(text, node.SpanStart);
            if (rest.Length == 0)
            {
                return new CompletedStatement([new TextEdit(end, line.End - end, missing + next)], end + missing.Length + next.Length);
            }
            return new CompletedStatement([new TextEdit(end, 0, missing), new TextEdit(line.End, 0, next)], line.End + missing.Length + next.Length);
        }

        /// <summary>A control statement (if, while, for, foreach, using, lock, else, do): its header's brackets closed, and
        /// braces with the caret between them when it has no body yet; the caret into its body when it has one, or that
        /// body completed when it is a statement on the same line.</summary>
        private CompletedStatement? Control(SyntaxNode statement, StatementSyntax body, SyntaxToken token, string after = "")
        {
            if (!Before(token, body))
            {
                return null;
            }
            SyntaxToken last = (body.IsMissing ? statement.GetLastToken() : body.GetFirstToken().GetPreviousToken());
            string closing = Missing(last, statement, statement: false, stopAt: body);
            bool unclosed = closing.Length > 0;
            int lastLine = text.GetLineFromOffset(last.Span.End);
            // Half a header and a statement on a line of its own after it: that statement is not its body.
            bool bodiless = body.IsMissing || unclosed && body is not BlockSyntax && text.GetLineFromOffset(body.SpanStart) > lastLine;
            if (bodiless)
            {
                return Braced(statement, last.Span.End, closing, after);
            }
            return body switch
            {
                BlockSyntax block => Into(block.OpenBraceToken, block.CloseBraceToken, block.Statements.FirstOrDefault(), closing, last),
                _ when text.GetLineFromOffset(body.SpanStart) == lastLine => Complete(body, body.GetFirstToken()) ?? Ended(body),
                _ => closing.Length > 0 ? Braced(statement, last.Span.End, closing, after) : new CompletedStatement([], body.SpanStart),
            };
        }

        /// <summary>A method, constructor or local function: its parameters' parenthesis closed, then braces with the caret
        /// in them, or a semicolon for one that has no body (abstract, extern, partial, in an interface).</summary>
        private CompletedStatement? Method(SyntaxNode method, BlockSyntax? body, bool noBody, SyntaxToken token)
        {
            if (body is not null)
            {
                return Before(token, body) ? Into(body.OpenBraceToken, body.CloseBraceToken, body.Statements.FirstOrDefault(), string.Empty, default) : null;
            }
            if (noBody)
            {
                return Ended(method);
            }
            SyntaxToken last = method.GetLastToken();
            return Braced(method, last.Span.End, Missing(last, method, statement: false), string.Empty);
        }

        /// <summary>A type, namespace, switch, try, catch or finally: braces after its header when it has none, else the
        /// caret into them.</summary>
        private CompletedStatement? Braces(SyntaxNode node, SyntaxToken open, SyntaxToken close, SyntaxNode? first, SyntaxToken token)
        {
            if (!open.IsMissing && token.SpanStart >= open.SpanStart)
            {
                return null;
            }
            if (open.IsMissing)
            {
                SyntaxToken last = open.GetPreviousToken();
                return Braced(node, last.Span.End, Missing(last, node, statement: false), string.Empty);
            }
            SyntaxToken header = open.GetPreviousToken();
            return Into(open, close, first, Missing(header, node, statement: false), header);
        }

        /// <summary>The header's closing brackets after its last token, then braces on lines of their own with the caret
        /// on the empty line between them.</summary>
        private CompletedStatement Braced(SyntaxNode statement, int at, string closing, string after)
        {
            string indent = TextNavigation.IndentOf(text, statement.SpanStart);
            string head = closing + eol + indent + "{" + eol + indent + unit;
            string inserted = head + eol + indent + "}" + after;
            return new CompletedStatement([new TextEdit(at, Spaces(at), inserted)], at + head.Length);
        }

        /// <summary>The caret into braces that are there: at the first thing inside them, or on a line opened between them
        /// when they hold nothing, the header's missing brackets closed first.</summary>
        private CompletedStatement Into(SyntaxToken open, SyntaxToken close, SyntaxNode? first, string closing, SyntaxToken header)
        {
            List<TextEdit> edits = closing.Length > 0 ? [new TextEdit(header.Span.End, 0, closing)] : [];
            if (first is not null)
            {
                return new CompletedStatement(edits, first.SpanStart + closing.Length);
            }
            // The brace's line: its own when it stands alone, the header's when it ends the header's line.
            string indent = TextNavigation.IndentOf(text, open.SpanStart);
            string head = eol + indent + unit;
            int from = open.Span.End;
            int to = close.IsMissing ? from + Spaces(from) : close.SpanStart;
            edits.Add(new TextEdit(from, to - from, head + eol + indent + (close.IsMissing ? "}" : string.Empty)));
            return new CompletedStatement(edits, from + closing.Length + head.Length);
        }

        /// <summary>The closing brackets (and for a statement its semicolon) Roslyn found missing after a token, in the
        /// order they close, up to <paramref name="stopAt"/>; a name or value it lacks is left for the person to type.</summary>
        private static string Missing(SyntaxToken last, SyntaxNode within, bool statement, SyntaxNode? stopAt = null)
        {
            StringBuilder missing = new();
            for (SyntaxToken next = last.GetNextToken(includeZeroWidth: true); next.RawKind != 0 && next.IsMissing; next = next.GetNextToken(includeZeroWidth: true))
            {
                if (next.Parent is null || !next.Parent.AncestorsAndSelf().Contains(within)
                    || stopAt is not null && next.Parent.AncestorsAndSelf().Contains(stopAt))
                {
                    break;
                }
                bool closes = next.Kind() switch
                {
                    SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.GreaterThanToken => true,
                    SyntaxKind.CloseBraceToken => statement && next.Parent is InitializerExpressionSyntax or AnonymousObjectCreationExpressionSyntax
                        or PropertyPatternClauseSyntax or SwitchExpressionSyntax,
                    SyntaxKind.SemicolonToken => statement,
                    _ => false,
                };
                if (closes)
                {
                    missing.Append(SyntaxFacts.GetText(next.Kind()));
                }
            }
            return missing.ToString();
        }

        /// <summary>The spaces and tabs from an offset to the next thing on its line, which what is inserted there replaces.</summary>
        private int Spaces(int at)
        {
            int end = at;
            while (end < text.Length && text[end] is ' ' or '\t')
            {
                end++;
            }
            return end - at;
        }

        /// <summary>Whether a method declared without a body is meant to have none.</summary>
        private static bool NeedsNoBody(BaseMethodDeclarationSyntax method) =>
            method.Modifiers.Any(m => m.Kind() is SyntaxKind.AbstractKeyword or SyntaxKind.ExternKeyword or SyntaxKind.PartialKeyword)
            || method.Parent is InterfaceDeclarationSyntax && !method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

        /// <summary>Whether the caret's token is before a body (in the header), or the body is not there yet.</summary>
        private static bool Before(SyntaxToken token, SyntaxNode body) => body.IsMissing || token.Span.End <= body.SpanStart;
    }
}
