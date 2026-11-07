using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>The options every in-process parse of a C# file is made with: the preprocessor symbols and C# version its
/// project compiles it with (<see cref="Defining"/>), or for a file in no project the symbols a Debug build of any .NET
/// project defines, at the newest C# the parser reads (<see cref="Default"/>). One options object a set of symbols and a
/// version, so the files of one framework share theirs and a parse knows by reference whether they changed.</summary>
public static class CSharpParsing
{
    private static readonly ConcurrentDictionary<(LanguageVersion Version, string Symbols), CSharpParseOptions> Made = new();

    /// <summary>The most text changed that is measured only around itself (<see cref="TypedMayGoDeep"/>): a person's typing, which
    /// can't take a file's brackets anywhere a stack can't hold, and costs what it changed.</summary>
    private const int MostTyped = 1000;

    /// <summary>How far past what typing changed the text is read for what it opened to close, before the whole text is.</summary>
    private const int MostReadOn = 64 * 1024;

    [ThreadStatic]
    private static long typedRead;

    /// <summary>How many characters measuring typing read on this thread (<see cref="TypedMayGoDeep"/>).</summary>
    internal static long TypedRead => typedRead;

    /// <summary>What's said where a text goes too deep (<see cref="DeepAt(SyntaxTree)"/>).</summary>
    public const string TooDeep = "Passenger read no deeper than this, so the file is read as text: it has no colours, folds or C# language server, "
        + "and what it declares reads as missing";

    /// <summary>A file in no project's.</summary>
    public static CSharpParseOptions Default { get; } = Defining(["DEBUG", "TRACE", "NET", "NETCOREAPP"], null);

    /// <summary>A text's tree, every in-process parse's way to one: its parser runs out of no stack (<see cref="DeepText"/>),
    /// and a text nested too deeply for it is a tree of nothing, its spans still the text's.</summary>
    public static SyntaxTree Parse(string text, CSharpParseOptions? options = null, string path = "") => Parse(SourceText.From(text), options, path);

    /// <inheritdoc cref="Parse(string, CSharpParseOptions?, string)"/>
    public static SyntaxTree Parse(SourceText text, CSharpParseOptions? options = null, string path = "") =>
        DeepText.Read(text.Length, () => CSharpSyntaxTree.ParseText(CodeBrackets.DeepAt(text, options) is int at ? new Unread(text.Length, at) : text, options, path));

    /// <summary>A tree as a text changed from its own has it, the parser reading again only what changed.</summary>
    public static SyntaxTree Changed(SyntaxTree tree, SourceText text)
    {
        IReadOnlyList<TextChangeRange> changes = text.GetChangeRanges(tree.GetText());
        bool whole = changes.Sum(c => (long)c.NewLength) > MostTyped || TypedMayGoDeep(tree, text, changes);
        CSharpParseOptions options = (CSharpParseOptions)tree.Options;
        return DeepText.Read(text.Length, () => whole && CodeBrackets.DeepAt(text, options) is int at
            ? CSharpSyntaxTree.ParseText(new Unread(text.Length, at), options, tree.FilePath)
            : tree.WithChangedText(text));
    }

    /// <summary>Whether typing may have taken a text too deep, so the whole is read, as read from the statement around what it
    /// changed that no expression holds, else its member, on past the change until nothing it opened is open where the old
    /// text was code: a parser reads ahead from a <c>?[</c> to its expression's end, which typing can join to the next, and a
    /// quote or a comment typed or taken away changes how what follows reads. A change of words and spaces changes neither.</summary>
    internal static bool TypedMayGoDeep(SyntaxTree tree, SourceText text, IReadOnlyList<TextChangeRange> changes)
    {
        SourceText old = tree.GetText();
        (int first, int changedEnd, int shift) = (-1, -1, 0);
        foreach (TextChangeRange change in changes)
        {
            TextSpan made = new(change.Span.Start + shift, change.NewLength);
            if (Touches(old, change.Span) || Touches(text, made))
            {
                first = first < 0 ? change.Span.Start : first;
                changedEnd = made.End;
            }
            shift += change.NewLength - change.Span.Length;
        }
        if (first < 0)
        {
            return false;
        }
        SyntaxNode root = tree.GetRoot();
        int start = SafeStart(root, first);
        start = New(changes, start);
        int lineStart = start;
        while (lineStart > 0 && !SyntaxFacts.IsNewLine(text[lineStart - 1]))
        {
            lineStart--;
        }
        bool written = false;
        for (int at = lineStart; at < start && !written; at++)
        {
            written = text[at] is not (' ' or '\t');
        }
        Brackets brackets = new() { CSharp = true, StartsMidLine = start > lineStart, StartsAfterText = written };
        char[] stretch = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            for (int at = start; at < text.Length;)
            {
                int count = Math.Min(at < changedEnd ? Math.Min(stretch.Length, changedEnd - at) : 256, text.Length - at);
                text.CopyTo(at, stretch, 0, count);
                brackets.Read(stretch.AsSpan(0, count));
                at += count;
                typedRead += count;
                if (brackets.TooDeep || at - changedEnd > MostReadOn)
                {
                    return true;
                }
                if (at >= changedEnd && brackets.Settled && CodeAt(root, Old(changes, at)))
                {
                    return false;
                }
            }
            return false;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(stretch);
        }
    }

    /// <summary>Where an old place lies in the changed text, before what was put in at it.</summary>
    private static int New(IReadOnlyList<TextChangeRange> changes, int at)
    {
        int moved = 0;
        foreach (TextChangeRange change in changes)
        {
            if (change.Span.Start >= at)
            {
                break;
            }
            moved += change.Span.End <= at ? change.NewLength - change.Span.Length : 0;
        }
        return at + moved;
    }

    /// <summary>Where a place in the changed text lay in the old one, a place in what was put in being where that went.</summary>
    private static int Old(IReadOnlyList<TextChangeRange> changes, int at)
    {
        int moved = 0;
        foreach (TextChangeRange change in changes)
        {
            int start = change.Span.Start + moved;
            if (at < start)
            {
                break;
            }
            if (at < start + change.NewLength)
            {
                return change.Span.Start;
            }
            moved += change.NewLength - change.Span.Length;
        }
        return at - moved;
    }

    /// <summary>Whether a span holds anything but words and spaces, which may read as a link, a closer, a quote or a comment.</summary>
    private static bool Touches(SourceText text, TextSpan span)
    {
        for (int at = span.Start; at < span.End; at++)
        {
            if (text[at] is not (' ' or '\t' or '_') && !char.IsLetterOrDigit(text[at]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Where reading may start for a change at a place, no expression open there: the innermost statement around it
    /// that no expression holds, else its member, else what of its type or namespace it lies in.</summary>
    private static int SafeStart(SyntaxNode root, int at)
    {
        SyntaxToken token = root.FindToken(Math.Min(at, root.FullSpan.End));
        SyntaxNode? statement = null;
        SyntaxNode? within = null;
        for (SyntaxNode? node = token.Parent; node is not null; within = node, node = node.Parent)
        {
            switch (node)
            {
                case ExpressionSyntax:
                    statement = null;
                    break;
                case StatementSyntax when statement is null:
                    statement = node;
                    break;
                case BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax or CompilationUnitSyntax:
                    return (statement ?? within)?.FullSpan.Start ?? token.FullSpan.Start;
                case MemberDeclarationSyntax:
                    return (statement ?? node).FullSpan.Start;
            }
        }
        return 0;
    }

    /// <summary>Whether a place in a tree's text lies in code, not within a string or a comment, where reading the text
    /// changed and the old one alike goes on as one.</summary>
    private static bool CodeAt(SyntaxNode root, int at)
    {
        if (at >= root.FullSpan.End)
        {
            return true;
        }
        SyntaxToken token = root.FindToken(at);
        if (token.Span.Contains(at))
        {
            return at == token.SpanStart || !token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.CharacterLiteralToken)
                && !token.IsKind(SyntaxKind.InterpolatedStringTextToken) && !token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                && !token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) && !token.IsKind(SyntaxKind.Utf8StringLiteralToken)
                && !token.IsKind(SyntaxKind.Utf8SingleLineRawStringLiteralToken) && !token.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken);
        }
        SyntaxTrivia trivia = root.FindTrivia(at);
        return at == trivia.SpanStart || trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia);
    }

    /// <summary>Whether a person's text reads as a type's name.</summary>
    public static bool ReadsAsType(string text) => !DeepText.TooDeep(text, csharp: true) && DeepText.Read(text.Length, () => !SyntaxFactory.ParseTypeName(text).ContainsDiagnostics);

    /// <summary>Where a tree's text went too deep, read as nothing; null for a text read.</summary>
    public static int? DeepAt(SyntaxTree tree) => tree.TryGetText(out SourceText? text) && text is Unread unread ? unread.At : null;

    /// <summary>Where a person's text as it stands goes too deep, null where it doesn't: asked before it goes to a reader with
    /// no such bound of its own.</summary>
    public static int? DeepAt(TextSnapshot text, CSharpParseOptions options)
    {
        SnapshotText read = new(text);
        return DeepText.Read(text.Length, () => CodeBrackets.DeepAt(read, options));
    }

    /// <summary>What's parsed in place of a text too deep: nothing, its length, and where it went too deep.</summary>
    private sealed class Unread(int length, int at) : SourceText
    {
        public int At => at;

        public override Encoding? Encoding => null;

        public override int Length => length;

        public override char this[int position] => ' ';

        public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count) => destination.AsSpan(destinationIndex, count).Fill(' ');
    }

    /// <summary>The options defining these symbols, each once, any that is not an identifier left out as the compiler leaves
    /// it out, at a C# version as a project's LangVersion writes it ("12", "latest"); one the parser does not know (a newer
    /// C#'s), or none, is its preview, the newest it reads.</summary>
    public static CSharpParseOptions Defining(IEnumerable<string> symbols, string? languageVersion)
    {
        string[] valid = [.. symbols.Where(SyntaxFacts.IsValidIdentifier).Distinct(StringComparer.Ordinal)];
        LanguageVersion version = languageVersion is not null && LanguageVersionFacts.TryParse(languageVersion, out LanguageVersion written) ? written : LanguageVersion.Preview;
        return Made.GetOrAdd((version, string.Join(';', valid)),
            static (key, defined) => new CSharpParseOptions(key.Version, DocumentationMode.Parse, preprocessorSymbols: defined), valid);
    }
}
