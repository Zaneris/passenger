using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;

namespace Passenger.Languages.CSharp;

/// <summary>Every in-process parse of C#, and the options it is made with: a parse runs on a stack it can't run out of
/// (<see cref="DeepText"/>), and a text whose brackets go past what a parser is given is read as nothing, its strings,
/// comments and the branches its conditions leave out apart, as a parser recurses only through what it reads as code. The
/// text as it stands is read first (<see cref="Brackets"/>), which real code passes at the cost of a search; only what that
/// finds deep is read by the lexer, the parser's own, so nothing a parser reads as code passes for a string. An expression
/// or a type's name that search finds shallow is read on the asking thread. Compiled into each assembly that parses.</summary>
internal static class CodeBrackets
{
    /// <summary>The most brackets a text holds open at once and is read on whatever thread asks, a level's two kilobytes
    /// within what that thread is sure to have left (<see cref="RuntimeHelpers.TryEnsureSufficientExecutionStack"/>).</summary>
    private const int MostOnAnyStack = 32;

    private static readonly ConcurrentDictionary<(LanguageVersion Version, string Symbols), CSharpParseOptions> Made = new();

    /// <summary>A text's tree: a text nested too deeply for the parser is a tree of nothing, its spans still the text's.</summary>
    public static SyntaxTree Parse(SourceText text, CSharpParseOptions? options = null, string path = "") =>
        DeepText.Read(text.Length, () => CSharpSyntaxTree.ParseText(DeepAt(text, options) is int at ? new Unread(text.Length, at) : text, options, path));

    /// <summary>Where a tree's text went too deep, read as nothing; null for a text read.</summary>
    public static int? DeepAt(SyntaxTree tree) => tree.TryGetText(out SourceText? text) && text is Unread unread ? unread.At : null;

    /// <summary>A text read as an expression on its own; null for one nested too deeply for the parser.</summary>
    public static ExpressionSyntax? Expression(string text) => Read(text, () => SyntaxFactory.ParseExpression(text));

    /// <summary>A text read as a type's name on its own; null for one nested too deeply for the parser.</summary>
    public static TypeSyntax? TypeName(string text) => Read(text, () => SyntaxFactory.ParseTypeName(text));

    /// <summary>Whether a text reads as a type's name.</summary>
    public static bool ReadsAsType(string text) => TypeName(text) is { ContainsDiagnostics: false };

    /// <summary>The options defining these symbols, each once, any that is not an identifier left out as the compiler leaves
    /// it out, at a C# version as a project's LangVersion writes it ("12", "latest"); one the parser does not know (a newer
    /// C#'s), one naming the newest C# of the project's compiler (latest, latestMajor, default), which may be past the
    /// parser's, or none, is its preview, the newest it reads. One options object a set of symbols and a version, so the
    /// files of one framework share theirs and a parse knows by reference whether they changed.</summary>
    public static CSharpParseOptions Defining(IEnumerable<string> symbols, string? languageVersion)
    {
        string[] valid = [.. symbols.Where(SyntaxFacts.IsValidIdentifier).Distinct(StringComparer.Ordinal)];
        LanguageVersion version = languageVersion is not null && LanguageVersionFacts.TryParse(languageVersion, out LanguageVersion written)
            && written is not (LanguageVersion.Latest or LanguageVersion.LatestMajor or LanguageVersion.Default) ? written : LanguageVersion.Preview;
        return Made.GetOrAdd((version, string.Join(';', valid)),
            static (key, defined) => new CSharpParseOptions(key.Version, DocumentationMode.Parse, preprocessorSymbols: defined), valid);
    }

    /// <summary>What a reader that recurses as deep as a C# text nests makes of it: on the asking thread where the text's
    /// brackets nest shallowly, as nearly every expression and type's name does, else on a stack sized for it.</summary>
    public static T OnStack<T>(string text, Func<T> read) => Shallow(Measured(SourceText.From(text))) ? read() : DeepText.Read(text.Length, read);

    /// <summary>Where a text first goes too deep, null for one that doesn't, asked on a stack of its characters' (<see
    /// cref="DeepText.Read"/>): the lexer recurses through an interpolation's brackets, so a text past what a stack <see
    /// cref="DeepText.Holds"/> stays as deep as it reads.</summary>
    public static int? DeepAt(SourceText text, CSharpParseOptions? options) => DeepAt(text, options, Measured(text));

    /// <summary>Where a text first goes too deep, from its brackets as it stands (<see cref="Measured"/>).</summary>
    private static int? DeepAt(SourceText text, CSharpParseOptions? options, Brackets measured) =>
        !measured.TooDeep ? null
        : measured.TooDeepToLex || !DeepText.Holds(text.Length) ? measured.DeepAt
        : TokensDeepAt(text.ToString(), options);

    /// <summary>A text's brackets as it stands, read a stretch at a time on any stack: real code passes at the cost of a search.</summary>
    private static Brackets Measured(SourceText text)
    {
        char[] stretch = ArrayPool<char>.Shared.Rent(4096);
        Brackets brackets = new() { CSharp = true };
        for (int at = 0; at < text.Length && !brackets.TooDeepToLex; at += stretch.Length)
        {
            int count = Math.Min(stretch.Length, text.Length - at);
            text.CopyTo(at, stretch, 0, count);
            brackets.Read(stretch.AsSpan(0, count));
        }
        ArrayPool<char>.Shared.Return(stretch);
        return brackets;
    }

    /// <summary>Whether a text's brackets as it stands nest shallowly enough to read it on the asking thread.</summary>
    private static bool Shallow(Brackets measured) =>
        !measured.TooDeep && measured.Reached <= MostOnAnyStack && RuntimeHelpers.TryEnsureSufficientExecutionStack();

    /// <summary>A text read on its own (<paramref name="parse"/>), on the asking thread where it nests shallowly, else on a
    /// stack sized for it; null for one nested too deeply for the parser.</summary>
    private static T? Read<T>(string text, Func<T> parse) where T : class
    {
        SourceText source = SourceText.From(text);
        Brackets measured = Measured(source);
        return Shallow(measured) ? parse() : DeepText.Read(text.Length, () => DeepAt(source, null, measured) is null ? parse() : null);
    }

    /// <summary>A text's brackets as its tokens have them. An interpolated string is a token to the lexer and code to the
    /// parser, as a documentation comment is trivia to one and read by the other, so each is read on its own, the text around it
    /// left as it was: what a template leaves open doesn't add up over a file, and a string's text closes nothing in its holes. A <c>?</c> is a link only before
    /// a <c>.</c> or a <c>[</c>, where it is a conditional access, and <c>not</c> only as written, never escaped. A token after
    /// a closing brace that no expression goes on with ends the conditional accesses read ahead through at its level.</summary>
    private static int? TokensDeepAt(string text, CSharpParseOptions? options)
    {
        Brackets brackets = new() { CSharp = true, Tokens = true };
        SyntaxKind before = SyntaxKind.None;
        foreach (SyntaxToken token in SyntaxFactory.ParseTokens(text, options: options))
        {
            if ((DeepIn(token.LeadingTrivia, ref brackets) ?? DeepIn(token.TrailingTrivia, ref brackets)) is int inTrivia)
            {
                return inTrivia;
            }
            if (before == SyntaxKind.CloseBraceToken && !GoesOn(token))
            {
                brackets.Ended();
            }
            switch (token.Kind())
            {
                case SyntaxKind.DotToken or SyntaxKind.OpenBracketToken when before == SyntaxKind.QuestionToken:
                    brackets.Read(token.IsKind(SyntaxKind.DotToken) ? "?." : "?[");
                    break;
                case SyntaxKind.OpenParenToken or SyntaxKind.CloseParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.CloseBracketToken
                    or SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken or SyntaxKind.SemicolonToken
                    or SyntaxKind.LessThanToken or SyntaxKind.GreaterThanToken or SyntaxKind.GreaterThanEqualsToken or SyntaxKind.EqualsGreaterThanToken:
                    brackets.Read(token.Text);
                    break;
                case SyntaxKind.IdentifierToken when token.Text == "not":
                    brackets.Read(token.Text);
                    break;
                case SyntaxKind.InterpolatedStringToken when DeepOnItsOwn(token.Text, brackets with { Tokens = false }):
                    return token.SpanStart;
            }
            if (brackets.TooDeep)
            {
                return token.SpanStart;
            }
            before = token.Kind();
        }
        return null;
    }

    /// <summary>Whether an expression may go on with a token after a closing brace: an operator, a bracket, <c>switch</c>,
    /// <c>with</c>, a comma and a pattern's or a query's words may, where any other word, a semicolon or a closer ends it.</summary>
    private static bool GoesOn(SyntaxToken token) => token.Kind() switch
    {
        SyntaxKind.IdentifierToken => token.Text is "with" or "and" or "or" or "not" or "when" or "select" or "where" or "group" or "by"
            or "into" or "orderby" or "join" or "let" or "on" or "equals" or "ascending" or "descending" or "from",
        SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.CloseParenToken
            or SyntaxKind.CloseBracketToken or SyntaxKind.EndOfFileToken => false,
        SyntaxKind kind when SyntaxFacts.IsKeywordKind(kind) => kind is SyntaxKind.SwitchKeyword or SyntaxKind.IsKeyword or SyntaxKind.AsKeyword or SyntaxKind.InKeyword,
        _ => true,
    };

    /// <summary>Reads the conditions among trivia into what's counted: where they or a documentation comment go too deep.</summary>
    private static int? DeepIn(SyntaxTriviaList trivia, ref Brackets brackets)
    {
        foreach (SyntaxTrivia one in trivia)
        {
            switch (one.Kind())
            {
                case SyntaxKind.IfDirectiveTrivia or SyntaxKind.ElifDirectiveTrivia or SyntaxKind.EndIfDirectiveTrivia:
                    brackets.Read(one.ToFullString());
                    if (brackets.TooDeep)
                    {
                        return one.SpanStart;
                    }
                    break;
                case SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia
                    when DeepOnItsOwn(one.ToFullString(), brackets) || ElementsTooDeep(one.GetStructure(), brackets):
                    return one.SpanStart;
            }
        }
        return null;
    }

    /// <summary>Whether a documentation comment's elements, each a level of the parser that reads it, nest too deep from where
    /// the brackets stand, a copy leaving them as they were. An element's tags, each closed, are no mark of how deep it is.</summary>
    private static bool ElementsTooDeep(SyntaxNode? comment, Brackets brackets)
    {
        brackets.Nest(ElementDepth(comment));
        return brackets.TooDeep;
    }

    /// <summary>How many elements a documentation comment's deepest one sits within, itself among them, read with a stack of its
    /// own rather than the thread's.</summary>
    private static int ElementDepth(SyntaxNode? comment)
    {
        int deepest = 0;
        Stack<(SyntaxNode Node, int Depth)> nodes = new();
        if (comment is not null)
        {
            nodes.Push((comment, 0));
        }
        while (nodes.TryPop(out (SyntaxNode Node, int Depth) at))
        {
            int depth = at.Depth + (at.Node.IsKind(SyntaxKind.XmlElement) ? 1 : 0);
            deepest = Math.Max(deepest, depth);
            foreach (SyntaxNode child in at.Node.ChildNodes())
            {
                nodes.Push((child, depth));
            }
        }
        return deepest;
    }

    /// <summary>Whether a text goes too deep from where the brackets stand, which a copy leaves as they were.</summary>
    private static bool DeepOnItsOwn(string text, Brackets brackets)
    {
        brackets.Read(text);
        return brackets.TooDeep;
    }

    /// <summary>What's parsed in place of a text too deep: nothing, its length, and where it went too deep.</summary>
    internal sealed class Unread(int length, int at) : SourceText
    {
        public int At => at;

        public override Encoding? Encoding => null;

        public override int Length => length;

        public override char this[int position] => ' ';

        public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count) => destination.AsSpan(destinationIndex, count).Fill(' ');
    }
}
