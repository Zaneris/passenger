using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Editor;

namespace Passenger.Languages.CSharp;

/// <summary>Colours C# from its syntax tree alone: keywords, literals, comments, directives, and
/// every name whose role the syntax settles (a declaration, an invoked method, a type in a type-only
/// position, a namespace). A name the syntax cannot place, like <c>a.b</c>, stays plain until the
/// language server's semantic colours say what it is.</summary>
internal static class CSharpClassifier
{
    private static readonly HashSet<SyntaxKind> ControlKeywords =
    [
        SyntaxKind.IfKeyword, SyntaxKind.ElseKeyword, SyntaxKind.WhileKeyword, SyntaxKind.ForKeyword,
        SyntaxKind.ForEachKeyword, SyntaxKind.DoKeyword, SyntaxKind.SwitchKeyword, SyntaxKind.CaseKeyword,
        SyntaxKind.DefaultKeyword, SyntaxKind.BreakKeyword, SyntaxKind.ContinueKeyword, SyntaxKind.ReturnKeyword,
        SyntaxKind.GotoKeyword, SyntaxKind.ThrowKeyword, SyntaxKind.TryKeyword, SyntaxKind.CatchKeyword,
        SyntaxKind.FinallyKeyword, SyntaxKind.YieldKeyword, SyntaxKind.AwaitKeyword, SyntaxKind.WhenKeyword,
    ];

    private static readonly TokenKind[] Braces = [TokenKind.Brace1, TokenKind.Brace2, TokenKind.Brace3];

    /// <summary>The spans of the tokens (and the trivia) that reach into <paramref name="range"/>, sorted; a comment or
    /// an excluded block that starts before the range is included whole. It finds the first token by position and walks
    /// token by token, never through a list of siblings, so a class with ten thousand members costs what the lines on
    /// screen cost. A bracket's depth is counted on from the first token's (<see cref="DepthBefore"/>): an opener at
    /// the depth it opens at, its closer at the same, a stray closer at none below the outermost.</summary>
    public static List<SyntaxSpan> Classify(SyntaxNode root, TextSpan range)
    {
        List<SyntaxSpan> spans = [];
        SyntaxToken token = root.FindToken(Math.Min(range.Start, Math.Max(0, root.FullSpan.End - 1)));
        int depth = DepthBefore(token);
        // The walk steps over the file's end (no width), so its trivia (a comment never closed) is read after it.
        SyntaxToken end = root.GetLastToken(includeZeroWidth: true);
        bool endRead = false;
        while (token.RawKind != 0 && token.FullSpan.Start <= range.End)
        {
            endRead |= token == end;
            AddTrivia(token.LeadingTrivia, range, spans);
            // A skipped token is trivia, not one of the tree's tokens: never a bracket.
            int bracket = token.Parent is { IsStructuredTrivia: true } ? 0 : Bracket(token);
            if (bracket > 0)
            {
                Add(spans, token.SpanStart, token.Span.Length, Braces[depth % Braces.Length]);
                depth++;
            }
            else if (bracket < 0)
            {
                depth = Math.Max(0, depth - 1);
                Add(spans, token.SpanStart, token.Span.Length, Braces[depth % Braces.Length]);
            }
            else
            {
                AddToken(token, spans);
            }
            AddTrivia(token.TrailingTrivia, range, spans);
            token = Next(root, token);
        }
        if (!endRead && end.RawKind != 0 && end.Span.Length == 0 && end.FullSpan.Start <= range.End)
        {
            AddTrivia(end.LeadingTrivia, range, spans);
        }
        return spans;
    }

    /// <summary>How many brackets are open before a token: those its ancestors hold before it, each node's own
    /// brackets being its direct children, so a lookup reads the nodes around it rather than every token of the file
    /// before it, which a new parse would build anew. The same as counting every bracket from the file's start
    /// wherever each node's brackets pair within it; where they do not (one missing as it is typed), that bracket
    /// counts inside its node and not beyond it.</summary>
    private static int DepthBefore(SyntaxToken token)
    {
        int depth = 0;
        SyntaxNodeOrToken on = token;
        for (SyntaxNode? parent = token.Parent; parent is not null; on = parent, parent = parent.Parent)
        {
            if (parent.ChildNodesAndTokens().Count > ManyChildren)
            {
                depth += Opened(Brackets.GetValue(parent, OwnBrackets), on.FullSpan.Start);
                continue;
            }
            foreach (SyntaxNodeOrToken child in parent.ChildNodesAndTokens())
            {
                if (child == on)
                {
                    break;
                }
                if (child.IsToken)
                {
                    depth += Bracket(child.AsToken());
                }
            }
        }
        return Math.Max(0, depth);
    }

    /// <summary>Past this many children a node's own brackets are found once and kept (<see cref="Brackets"/>) rather
    /// than its children read up to a token each time: a list of thousands of elements on screen.</summary>
    private const int ManyChildren = 32;

    /// <summary>A node's own brackets, for a node with many children: where each starts and how many are open after it.</summary>
    private static readonly ConditionalWeakTable<SyntaxNode, int[]> Brackets = new();

    private static int[] OwnBrackets(SyntaxNode node)
    {
        List<int> found = [];
        int open = 0;
        foreach (SyntaxNodeOrToken child in node.ChildNodesAndTokens())
        {
            if (child.IsToken && Bracket(child.AsToken()) is int bracket and not 0)
            {
                open += bracket;
                found.Add(child.FullSpan.Start);
                found.Add(open);
            }
        }
        return [.. found];
    }

    /// <summary>How many of a node's own brackets (<see cref="OwnBrackets"/>) before a child are open.</summary>
    private static int Opened(int[] brackets, int child)
    {
        int open = 0;
        for (int i = 0; i < brackets.Length && brackets[i] < child; i += 2)
        {
            open = brackets[i + 1];
        }
        return open;
    }

    /// <summary>The token after this one, as <c>GetNextToken(includeZeroWidth: false, includeSkipped: true)</c> finds it,
    /// but found from the root by position, which halves through a long list where GetNextToken walks its siblings from the
    /// first: in a collection of a hundred thousand elements each token on screen would cost a walk of that many. Next to
    /// skipped tokens, Roslyn's own walk steps into them.</summary>
    private static SyntaxToken Next(SyntaxNode root, SyntaxToken token)
    {
        SyntaxToken at = token;
        while (true)
        {
            if (at.Parent is not { IsStructuredTrivia: false } || Skipped(at.TrailingTrivia))
            {
                return at.GetNextToken(includeZeroWidth: false, includeSkipped: true);
            }
            int end = at.FullSpan.End;
            if (end >= root.FullSpan.End)
            {
                return default;
            }
            SyntaxToken next = root.FindToken(end);
            if (Skipped(next.LeadingTrivia))
            {
                return at.GetNextToken(includeZeroWidth: false, includeSkipped: true);
            }
            if (next.Span.Length > 0)
            {
                return next;
            }
            at = next;
        }
    }

    private static bool Skipped(SyntaxTriviaList trivia)
    {
        foreach (SyntaxTrivia t in trivia)
        {
            if (t.IsKind(SyntaxKind.SkippedTokensTrivia))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>1 for an opening bracket, -1 for a closing one, 0 for anything else: parentheses, square brackets,
    /// braces, and the angle brackets of type arguments and parameters (not a comparison's).</summary>
    internal static int Bracket(SyntaxToken token) => token.Kind() switch
    {
        SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenBraceToken => token.IsMissing ? 0 : 1,
        SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken => token.IsMissing ? 0 : -1,
        SyntaxKind.LessThanToken when token.Parent is TypeArgumentListSyntax or TypeParameterListSyntax => 1,
        SyntaxKind.GreaterThanToken when token.Parent is TypeArgumentListSyntax or TypeParameterListSyntax => -1,
        _ => 0,
    };

    /// <summary>The trivia of a token that reach into a range, the first found by halving the list's positions: a run of
    /// thousands of <c>//</c> lines is the leading trivia of the one token after it, of which a screen's lines are a few.</summary>
    private static void AddTrivia(SyntaxTriviaList trivia, TextSpan range, List<SyntaxSpan> spans)
    {
        // A long list's positions are kept by Roslyn (its indexer reads them), a short one's summed: walked from its start.
        int count = trivia.Count;
        int first = 0;
        for (int last = count; count > 16 && first < last;)
        {
            int middle = (first + last) / 2;
            if (trivia[middle].FullSpan.End < range.Start)
            {
                first = middle + 1;
            }
            else
            {
                last = middle;
            }
        }
        for (int i = first; i < count; i++)
        {
            SyntaxTrivia t = trivia[i];
            if (t.FullSpan.Start > range.End)
            {
                break;
            }
            if (t.FullSpan.End < range.Start)
            {
                continue;
            }
            TokenKind? kind = t.Kind() switch
            {
                SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia => TokenKind.Comment,
                SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia => TokenKind.DocComment,
                SyntaxKind.DisabledTextTrivia => TokenKind.Excluded,
                _ when t.IsDirective => TokenKind.Preprocessor,
                _ => null,
            };
            if (kind is { } k)
            {
                // A doc comment's first /// is its own leading trivia, outside its Span; FullSpan has it.
                Add(spans, t.FullSpan.Start, t.FullSpan.Length, k);
            }
        }
    }

    private static void AddToken(SyntaxToken token, List<SyntaxSpan> spans)
    {
        SyntaxKind kind = token.Kind();
        if (SyntaxFacts.IsKeywordKind(kind))
        {
            Add(spans, token.SpanStart, token.Span.Length, ControlKeywords.Contains(kind) ? TokenKind.ControlKeyword : TokenKind.Keyword);
            return;
        }
        switch (kind)
        {
            case SyntaxKind.NumericLiteralToken:
                Add(spans, token.SpanStart, token.Span.Length, TokenKind.Number);
                return;
            case SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.InterpolatedStringTextToken:
                AddString(token, spans);
                return;
            case SyntaxKind.Utf8StringLiteralToken or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken
                or SyntaxKind.InterpolatedStringStartToken or SyntaxKind.InterpolatedStringEndToken
                or SyntaxKind.InterpolatedVerbatimStringStartToken or SyntaxKind.InterpolatedSingleLineRawStringStartToken
                or SyntaxKind.InterpolatedMultiLineRawStringStartToken or SyntaxKind.InterpolatedRawStringEndToken:
                Add(spans, token.SpanStart, token.Span.Length, TokenKind.String);
                return;
            case SyntaxKind.IdentifierToken:
                if (Identifier(token) is { } role)
                {
                    Add(spans, token.SpanStart, token.Span.Length, role);
                }
                return;
        }
    }

    /// <summary>A string with its escapes picked out, unless it is verbatim, where a backslash is
    /// just a backslash.</summary>
    private static void AddString(SyntaxToken token, List<SyntaxSpan> spans)
    {
        string text = token.Text;
        int start = token.SpanStart;
        bool verbatim = text.StartsWith('@') || (token.IsKind(SyntaxKind.InterpolatedStringTextToken) && IsVerbatimInterpolation(token));
        if (verbatim || !text.Contains('\\'))
        {
            Add(spans, start, text.Length, TokenKind.String);
            return;
        }
        int run = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                continue;
            }
            int length = EscapeLength(text, i);
            Add(spans, start + run, i - run, TokenKind.String);
            Add(spans, start + i, length, TokenKind.StringEscape);
            run = i + length;
            i += length - 1;
        }
        Add(spans, start + run, text.Length - run, TokenKind.String);
    }

    private static bool IsVerbatimInterpolation(SyntaxToken token) =>
        token.Parent?.Parent is InterpolatedStringExpressionSyntax outer && outer.StringStartToken.Text.Contains('@');

    private static int EscapeLength(string text, int at)
    {
        char next = text[at + 1];
        int digits = next switch
        {
            'x' => CountHex(text, at + 2, 4),
            'u' => 4,
            'U' => 8,
            _ => 0,
        };
        return Math.Min(2 + digits, text.Length - at);
    }

    private static int CountHex(string text, int from, int max)
    {
        int count = 0;
        while (count < max && from + count < text.Length && Uri.IsHexDigit(text[from + count]))
        {
            count++;
        }
        return count;
    }

    private static TokenKind? Identifier(SyntaxToken token) => token.Parent switch
    {
        ClassDeclarationSyntax => TokenKind.Class,
        RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? TokenKind.Struct : TokenKind.Class,
        StructDeclarationSyntax => TokenKind.Struct,
        InterfaceDeclarationSyntax => TokenKind.Interface,
        EnumDeclarationSyntax => TokenKind.Enum,
        // A union (C# 15), a struct holding one of its cases, told by its keyword: its syntax is still Roslyn's experiment.
        TypeDeclarationSyntax { Keyword.ValueText: "union" } => TokenKind.Struct,
        DelegateDeclarationSyntax => TokenKind.Delegate,
        TypeParameterSyntax => TokenKind.TypeParameter,
        MethodDeclarationSyntax or LocalFunctionStatementSyntax => TokenKind.Method,
        ConstructorDeclarationSyntax or DestructorDeclarationSyntax => TokenKind.Class,
        PropertyDeclarationSyntax => TokenKind.Property,
        EventDeclarationSyntax => TokenKind.Event,
        EnumMemberDeclarationSyntax => TokenKind.EnumMember,
        ParameterSyntax => TokenKind.Parameter,
        VariableDeclaratorSyntax declarator => declarator.Parent?.Parent switch
        {
            FieldDeclarationSyntax field => field.Modifiers.Any(SyntaxKind.ConstKeyword) ? TokenKind.Constant : TokenKind.Field,
            EventFieldDeclarationSyntax => TokenKind.Event,
            LocalDeclarationStatementSyntax local => local.IsConst ? TokenKind.Constant : TokenKind.Local,
            _ => TokenKind.Local,
        },
        SingleVariableDesignationSyntax or ForEachStatementSyntax or CatchDeclarationSyntax => TokenKind.Local,
        LabeledStatementSyntax => TokenKind.Label,
        SimpleNameSyntax name => Name(name),
        _ => null,
    };

    /// <summary>A name used rather than declared, placed by where it stands.</summary>
    private static TokenKind? Name(SimpleNameSyntax name)
    {
        if (name is IdentifierNameSyntax { Identifier.Text: "var" or "dynamic" or "unmanaged" or "notnull" } contextual
            && SyntaxFacts.IsInTypeOnlyContext(contextual))
        {
            return TokenKind.Keyword;
        }
        if (name is IdentifierNameSyntax { Identifier.Text: "nameof", Parent: InvocationExpressionSyntax })
        {
            return TokenKind.Keyword;
        }
        if (IsNamespace(name))
        {
            return TokenKind.Namespace;
        }
        if (IsInvoked(name))
        {
            return TokenKind.Method;
        }
        if (name.Parent is AttributeSyntax || (name.Parent is QualifiedNameSyntax { Parent: AttributeSyntax } q && q.Right == name))
        {
            return TokenKind.Class;
        }
        ExpressionSyntax outermost = name;
        while (outermost.Parent is QualifiedNameSyntax qualified && qualified.Right == outermost)
        {
            outermost = qualified;
        }
        bool rightmost = name.Parent is not QualifiedNameSyntax left || left.Right == name;
        return rightmost && SyntaxFacts.IsInTypeOnlyContext(outermost) ? TokenKind.Class : null;
    }

    private static bool IsInvoked(SimpleNameSyntax name) => name.Parent switch
    {
        InvocationExpressionSyntax call => call.Expression == name,
        MemberAccessExpressionSyntax member => member.Name == name && member.Parent is InvocationExpressionSyntax call && call.Expression == member,
        MemberBindingExpressionSyntax binding => binding.Parent is InvocationExpressionSyntax call && call.Expression == binding,
        _ => false,
    };

    /// <summary>A name inside a using directive's or a namespace declaration's dotted name.</summary>
    private static bool IsNamespace(SimpleNameSyntax name)
    {
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax)
        {
            node = node.Parent;
        }
        return node.Parent switch
        {
            UsingDirectiveSyntax directive => directive.Alias is null && directive.StaticKeyword.IsKind(SyntaxKind.None) && directive.NamespaceOrType == node,
            BaseNamespaceDeclarationSyntax declaration => declaration.Name == node,
            _ => false,
        };
    }

    private static void Add(List<SyntaxSpan> spans, int start, int length, TokenKind kind)
    {
        if (length > 0)
        {
            spans.Add(new SyntaxSpan(start, length, kind));
        }
    }
}
